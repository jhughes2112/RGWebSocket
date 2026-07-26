#nullable enable
//-------------------
// Reachable Games
// Copyright 2023
//-------------------

using DataCollection;
using Logging;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		// Use this to easily register endpoints for callbacks for normal HTTP requests, whereas all websocket upgrades will be handled by the RGConnectionManager that is passed in.
		public class RGWebServer
		{
			private readonly string            _url;
			private readonly string            _urlPathPrefix; // if this server is hosted at http://some.com/foo/bar, this is "/foo/bar/", for prefix-stripping request paths
			private readonly ILogging          _logger;
			private          RGWebSocketServer _httpServer;

			public WebSocketServerMetrics Metrics => _httpServer.Metrics; // distribution-oriented server metrics, updated live

			//-------------------

			// dataCollection is nullable ON PURPOSE: pass your IDataCollection derivative to feed prometheus, or null explicitly.
			// upgradeAuthorizer is nullable the same way: pass your Origin/auth gate to run BEFORE every websocket handshake is
			// accepted (denials are plain HTTP statuses and never cost a socket), or null explicitly for a public server.
			public RGWebServer(string url, int listenerThreads, int connectionTimeoutMS, int idleSeconds, RGConnectionManager connectionManager, ILogging logger, IDataCollection? dataCollection, RGWebSocketServer.UpgradeAuthorizer? upgradeAuthorizer)
			{
				_url    = url;
				_logger = logger;

				string[] urlParts = url.Split('/'); // When you have a url, you have protocol://domain:port/path/part/etc
				string   urlPath  = string.Join('/', urlParts, 3, urlParts.Length-3); // this leaves you with path/part/etc
				_urlPathPrefix    = "/" + urlPath;
				if (_urlPathPrefix.EndsWith("/", StringComparison.Ordinal) == false)
					_urlPathPrefix += "/";

				_httpServer = new RGWebSocketServer(listenerThreads, connectionTimeoutMS, idleSeconds, _url, HttpRequestHandler, connectionManager, _logger, dataCollection, upgradeAuthorizer);
			}

			//-------------------

			public void Start()
			{
				if (_httpServer.IsListening())
					throw new InvalidOperationException($"WebServer.Start is already listening at {_url}");

				try
				{
					_httpServer.StartListening(); // start listening AFTER we have registered the handlers
				}
				catch (Exception e)
				{
					if (e is HttpListenerException)
					{
						_logger.Log(EVerbosity.Error, "If you get an Access Denied error, open an ADMIN command shell and run:");
						_logger.Log(EVerbosity.Error, $"   netsh http add urlacl url={_url} user=\"{Environment.UserDomainName}\\{Environment.UserName}\"");
					}
					else
					{
						_logger.Log(EVerbosity.Error, $"Exception: {e}");
					}
					throw;
				}
				_logger.Log(EVerbosity.Warning, $"WebServer.Start listening at {_url}");
			}

			public async Task Shutdown()
			{
				await _httpServer.StopListening().ConfigureAwait(false);          // kill all the connections and abort any that don't die quietly
				_logger.Log(EVerbosity.Warning, $"WebServer.Shutdown at {_url}"); // Warning matches Start's level -- a normal shutdown is noteworthy, not an error
			}

			//-------------------
			// HTTP handlers
			//-------------------
			// This is the set of http endpoint handlers are kept.  "/metrics" -> Metrics.HandleMetricsRequest, for example.
			// The token fires when the request's deadline (the server's connectionTimeoutMS) passes: the server has already
			// answered 503 and moved on, so a handler doing long work should observe the token and STOP -- anything it
			// returns after cancellation is thrown away (never sent, never cached).
			public delegate Task<(int, string, byte[])> HTTPRequestHandler(HttpListenerContext context, CancellationToken token); // handlers should return (httpStatus, contentType, content) so we can handle errors gracefully

			// Per-endpoint authorization, run by the server on EVERY request -- including ones answered from the
			// response cache, which is the whole point: a handler that checks auth internally never runs on a cache
			// hit, so a cached endpoint's gate MUST live here instead.  Return null to admit the request, or a
			// ready-to-send (status, contentType, body) denial.  Denials are never cached.
			// The token fires at the request deadline.  OBSERVE IT: an authorizer typically calls a token service or
			// database, and abandoning the wait does not abandon that call -- without the token, a hung dependency keeps
			// piling up live work per request while the server has already answered.
			public delegate Task<(int, string, byte[])?> HTTPAuthorizer(HttpListenerContext context, CancellationToken token);

			// Endpoint tables are read on every request (thread pool tasks) and Register/Unregister may be called while
			// the server is serving, so ALL access goes through _endpointLock -- a Dictionary read racing a Remove is
			// undefined behavior, not just a stale answer.  The lock holds only for the lookup/mutation, never a handler call.
			private readonly object _endpointLock = new object();

			// Exact match endpoints - use dictionary for fast O(1) lookup
			private Dictionary<string, (HTTPRequestHandler handler, int cacheSeconds, bool cacheIgnoresQuery, HTTPAuthorizer? authorizer)> _exactEndpointHandlers = new Dictionary<string, (HTTPRequestHandler, int, bool, HTTPAuthorizer?)>();

			// Prefix match endpoints - use list for ordered checking, stored as (prefix, handler, cacheSeconds, cacheIgnoresQuery, authorizer) tuples
			private List<(string prefix, HTTPRequestHandler handler, int cacheSeconds, bool cacheIgnoresQuery, HTTPAuthorizer? authorizer)> _prefixEndpointHandlers = new List<(string, HTTPRequestHandler, int, bool, HTTPAuthorizer?)>();

			//-------------------
			// Outward-facing response cache: an endpoint registered with cacheSeconds > 0 has its successful (200) GET
			// responses cached by path+query (path alone with cacheIgnoresQuery) for that many seconds, so a herd of
			// identical requests costs ONE handler invocation instead of N -- it behaves like a rate limiter on expensive
			// public endpoints.  The cache is lazy: expiry is checked on the hit path, and a once-a-minute prune sweeps
			// out whatever went stale.  It is also BOUNDED on BOTH axes, because anything a remote caller can grow is an
			// attack surface and either bound alone has a hole: payloads over kCacheMaxEntryBytes are never cached, the
			// cache never exceeds kCacheMaxTotalBytes of total FOOTPRINT (bodies plus keys plus per-entry overhead --
			// charging bodies alone let a flood of empty responses under unique keys report zero usage) and never
			// exceeds kCacheMaxEntries entries, and abandoned (timed-out) requests never store anything.
			// NEVER flag an endpoint whose response depends on WHO is asking (Authorization headers, cookies, roles) --
			// the cache would happily serve one caller's authorized answer to the next caller.  Non-GET requests and
			// non-200 responses are never cached, so errors and actions always do the real work.
			private class CachedResponse
			{
				public int    _status;
				public string _contentType = string.Empty;
				public byte[] _content     = Array.Empty<byte>();
				public long   _expiresMs;
				public int    _footprint; // what this entry costs the budget: body + key + object overhead, computed once at store time
			}

			// What one cache entry costs beyond its body.  Charging ONLY the body was a hole: 10,000 cached empty
			// responses under 10,000 unique query strings reported 0 bytes used while pinning the keys, the entry
			// objects, and the dictionary slots.  The key is attacker-sized (a URL can be kilobytes), so it is
			// charged by its real length; the rest is a flat, deliberately generous estimate of the CachedResponse,
			// its string, and the dictionary bucket.
			private const    int    kCacheEntryOverheadBytes                    = 256;
			static private   int    EntryFootprint(string key, byte[] content) => content.Length + (key.Length * sizeof(char)) + kCacheEntryOverheadBytes;
			private readonly object _cacheLock = new object();
			private readonly Dictionary<string, CachedResponse> _responseCache = new Dictionary<string, CachedResponse>();
			private          long _cacheTotalBytes; // sum of every entry's FOOTPRINT (body + key + overhead), maintained by every add/remove
			private          long _nextPruneMs;
			private          long _nextCacheFullLogMs; // rate-limits the budget-full warning so an attack can't turn the log into a firehose
			private const    long kCachePruneIntervalMs = 60_000;
			private const    int  kCacheMaxEntryBytes   = 100*1024;     // a payload bigger than this is NEVER cached -- big files served through a cached endpoint must not become resident memory per unique URL
			private const    long kCacheMaxTotalBytes   = 32*1024*1024; // hard budget for the whole cache; when full (after pruning expired entries) new answers are served but NOT cached
			private const    int  kCacheMaxEntries      = 10_000;       // second, independent bound: entry COUNT.  Bytes alone can't stop a flood of tiny (or empty) responses under unique keys, and every entry costs a dictionary slot whatever its body weighs.

			// Observability: current cache footprint, for health pages and tests.  TotalBytes counts the whole footprint
			// (bodies, keys, and per-entry overhead), not just bodies -- see EntryFootprint.
			public int  CacheEntryCount { get { lock (_cacheLock) return _responseCache.Count; } }
			public long CacheTotalBytes { get { lock (_cacheLock) return _cacheTotalBytes; } }

			// Register an endpoint that matches exactly.  ALL policies are REQUIRED so every registration states them
			// explicitly: cacheSeconds 0 = never cached, N = successful GET responses are served from cache for N
			// seconds; cacheIgnoresQuery true = the query string is DROPPED from the cache key, so /file?a=1 and
			// /file?a=2 are the same entry -- use it whenever the query does not change the answer (file serving,
			// static pages), because otherwise every unique query string mints a fresh cache entry and an attacker can
			// mint them for free; authorizer null = public, non-null runs on EVERY request (cached or not) before
			// anything is served.  An endpoint whose RESPONSE varies by caller must still use cacheSeconds:0 -- the
			// authorizer makes gating cache-safe, but the cache still hands every admitted caller the same bytes.
			public void RegisterExactEndpoint(string urlPath, HTTPRequestHandler handler, int cacheSeconds, bool cacheIgnoresQuery, HTTPAuthorizer? authorizer)
			{
				lock (_endpointLock)
				{
					if (_exactEndpointHandlers.TryAdd(urlPath, (handler, cacheSeconds, cacheIgnoresQuery, authorizer)) == false)
					{
						_logger.Log(EVerbosity.Error, $"RegisterExactEndpoint {urlPath} is already defined.  Ignoring.");
					}
				}
				InvalidateCache(); // whatever is cached for this path was produced by the previous registration's handler and policy
			}

			// Register an endpoint that matches if the request path starts with the given prefix.  Policies as above (all required).
			public void RegisterPrefixEndpoint(string urlPrefix, HTTPRequestHandler handler, int cacheSeconds, bool cacheIgnoresQuery, HTTPAuthorizer? authorizer)
			{
				lock (_endpointLock)
				{
					// Check if this prefix is already registered
					for (int i = 0; i < _prefixEndpointHandlers.Count; i++)
					{
						if (_prefixEndpointHandlers[i].prefix == urlPrefix)
						{
							_logger.Log(EVerbosity.Error, $"RegisterPrefixEndpoint {urlPrefix} is already defined.  Ignoring.");
							return;
						}
					}

					_prefixEndpointHandlers.Add((urlPrefix, handler, cacheSeconds, cacheIgnoresQuery, authorizer));
				}
				InvalidateCache(); // whatever is cached under this prefix was produced by the previous registration's handler and policy
			}

			// Unregister an exact endpoint
			public void UnregisterExactEndpoint(string urlPath)
			{
				lock (_endpointLock)
				{
					if (_exactEndpointHandlers.Remove(urlPath) == false)
					{
						_logger.Log(EVerbosity.Error, $"UnregisterExactEndpoint {urlPath} not found to unregister.");
					}
				}
				InvalidateCache();
			}

			// Unregister a prefix endpoint
			public void UnregisterPrefixEndpoint(string urlPrefix)
			{
				lock (_endpointLock)
				{
					for (int i = 0; i < _prefixEndpointHandlers.Count; i++)
					{
						if (_prefixEndpointHandlers[i].prefix == urlPrefix)
						{
							_prefixEndpointHandlers.RemoveAt(i);
							return;
						}
					}
					_logger.Log(EVerbosity.Error, $"UnregisterPrefixEndpoint {urlPrefix} not found to unregister.");
				}
				InvalidateCache();
			}

			// Regular HTTP calls come here.  They are dispatched to any registered endpoints.  The token is the request's
			// deadline (owned by RGWebSocketServer): once it fires, the server has already answered 503 and this task is
			// running abandoned -- so past that point we produce NO side effects: nothing written, nothing cached.
			private async Task HttpRequestHandler(HttpListenerContext httpContext, CancellationToken token)
			{
				int     responseCode        = 500;
				string  responseContentType = "text/plain";
				byte[]? responseContent     = null;

				// Strip the hosting prefix off the FRONT of the path only.  (string.Replace would also mangle it if it appeared mid-path,
				// e.g. hosting at /api would break a request for /api/api-docs.)
				string path             = httpContext.Request.Url?.AbsolutePath ?? string.Empty;
				string relativeEndpoint = path;
				if (_urlPathPrefix.Length > 1)
				{
					if (path.StartsWith(_urlPathPrefix, StringComparison.Ordinal))
						relativeEndpoint = path.Substring(_urlPathPrefix.Length - 1); // keep the leading slash, e.g. /foo/metrics -> /metrics
					else if (path.Length == _urlPathPrefix.Length - 1 && _urlPathPrefix.StartsWith(path, StringComparison.Ordinal))
						relativeEndpoint = "/"; // a request for the hosting root itself, without the trailing slash
				}

				HTTPRequestHandler? handler           = null;
				HTTPAuthorizer?     authorizer        = null;
				int                 cacheSeconds      = 0;
				bool                cacheIgnoresQuery = false;

				lock (_endpointLock) // registrations may mutate these tables while requests are in flight
				{
					// First, try exact match (fastest - O(1) dictionary lookup)
					if (_exactEndpointHandlers.TryGetValue(relativeEndpoint, out (HTTPRequestHandler handler, int cacheSeconds, bool cacheIgnoresQuery, HTTPAuthorizer? authorizer) exact))
					{
						handler           = exact.handler;
						cacheSeconds      = exact.cacheSeconds;
						cacheIgnoresQuery = exact.cacheIgnoresQuery;
						authorizer        = exact.authorizer;
					}
					else
					{
						// If no exact match, check prefix matches (slower - O(n) list iteration).  LONGEST match wins, not
						// first-registered: with first-match, registering a broad public prefix ("/api/") before a narrow
						// protected one ("/api/admin/") silently shadows the narrow one AND its authorizer, so registration
						// ORDER decides whether a route is guarded.  That is an authorization bypass hiding in a list sort.
						int bestPrefixLength = -1;
						foreach (var (prefix, prefixHandler, prefixCacheSeconds, prefixCacheIgnoresQuery, prefixAuthorizer) in _prefixEndpointHandlers)
						{
							if (prefix.Length > bestPrefixLength && relativeEndpoint.StartsWith(prefix, StringComparison.Ordinal))
							{
								bestPrefixLength  = prefix.Length;
								handler           = prefixHandler;
								cacheSeconds      = prefixCacheSeconds;
								cacheIgnoresQuery = prefixCacheIgnoresQuery;
								authorizer        = prefixAuthorizer;
							}
						}
					}
				}

				if (handler != null)
				{
					// Authorization runs FIRST, on every request -- a cache hit must never skip the gate.  A denial is
					// sent as-is and never cached.
					(int, string, byte[])? deny = null;
					if (authorizer != null)
					{
						try
						{
							deny = await authorizer(httpContext, token).ConfigureAwait(false);
						}
						catch (OperationCanceledException) when (token.IsCancellationRequested)
						{
							return; // the deadline passed inside the gate; the server layer has already answered 503
						}
						catch (Exception e)
						{
							_logger.Log(EVerbosity.Error, $"Exception in endpoint authorizer {RGWebSocketServer.SafeUrl(httpContext)} {e}");
							deny = (500, "text/plain", System.Text.Encoding.UTF8.GetBytes("500 Internal Server Error")); // fail CLOSED: an authorizer that throws admits nobody
						}
					}
					if (deny != null)
					{
						(responseCode, responseContentType, responseContent) = deny.Value;
					}
					else
					{
						long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
						PruneCacheIfDue(nowMs);
						// Cache key is path+query by default: /api/search?q=al and /api/search?q=bob are different answers.
						// An endpoint registered with cacheIgnoresQuery keys on the path alone, so every query permutation
						// collapses to ONE entry -- otherwise each unique query string is a fresh entry an attacker mints for free.
						string cacheKey  = (cacheIgnoresQuery ? httpContext.Request.Url?.AbsolutePath : httpContext.Request.Url?.PathAndQuery) ?? relativeEndpoint;
						bool   cacheable = cacheSeconds > 0 && httpContext.Request.HttpMethod=="GET";
						if (cacheable && TryGetCachedResponse(cacheKey, nowMs, out int cachedStatus, out string cachedType, out byte[] cachedContent))
						{
							responseCode        = cachedStatus;
							responseContentType = cachedType;
							responseContent     = cachedContent;
						}
						else
						{
							try
							{
								(responseCode, responseContentType, responseContent) = await handler(httpContext, token).ConfigureAwait(false);
								if (cacheable && responseCode == 200 && responseContent != null && token.IsCancellationRequested == false) // an abandoned request's answer is dead: caching it would let timed-out work fill memory anyway
									StoreCachedResponse(cacheKey, nowMs, nowMs + cacheSeconds * 1000L, responseCode, responseContentType, responseContent);
							}
							catch (OperationCanceledException) when (token.IsCancellationRequested)
							{
								return; // the deadline passed mid-handler; the server already 503'd, so there is nothing to write and nothing to log
							}
							catch (Exception e)
							{
								// Log the details, but never send exception text (stack frames, paths, internals) to whoever is on the other end of the socket.
								_logger.Log(EVerbosity.Error, $"Exception in endpoint handler {RGWebSocketServer.SafeUrl(httpContext)} {e}");
								responseCode        = 500;
								responseContentType = "text/plain";
								responseContent     = System.Text.Encoding.UTF8.GetBytes("500 Internal Server Error");
							}
						}
					}
				}
				else
				{
					responseCode        = 404;
					responseContentType = "text/plain";
					responseContent     = System.Text.Encoding.UTF8.GetBytes($"No endpoint found for {path}"); // echo the PATH only -- reflecting the query string hands attackers a free canvas in the response body
				}

				if (token.IsCancellationRequested)
					return; // abandoned: the server layer owns the response now (503/abort), writing here would race it

				try
				{
					httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff"; // the declared content type is the truth; never let a browser second-guess it into something executable
					httpContext.Response.ContentType                       = responseContentType;
					httpContext.Response.StatusCode                        = responseCode;
					if (responseContent != null)
					{
						httpContext.Response.ContentLength64 = responseContent.Length;
						await httpContext.Response.OutputStream.WriteAsync(responseContent, 0, responseContent.Length).ConfigureAwait(false);
					}
				}
				catch (Exception e) when (TransportTeardown.IsExpected(e))
				{
					// The client went away before we finished replying: it aborted the request, closed its tab, or HttpListener
					// already disposed the response (e.g. after auto-411ing a bodyless POST).  The response is gone -- nothing to
					// send, nothing actionable -- so log it quietly instead of a loud Error+stack (contract: TransportTeardown.cs).
					_logger.Log(EVerbosity.Debug, $"Http response closed by client during write.  {RGWebSocketServer.SafeUrl(httpContext)} {e.GetType().Name}: {e.Message}");
				}
				catch (Exception e)
				{
					_logger.Log(EVerbosity.Error, $"Exception while trying to write to http response.  {RGWebSocketServer.SafeUrl(httpContext)} {e}");
				}
			}

			//-------------------
			// Response cache internals (invisible outside this class -- endpoints only ever declare a duration).

			// A live cached answer for this exact path+query, if one exists.
			private bool TryGetCachedResponse(string key, long nowMs, out int status, out string contentType, out byte[] content)
			{
				lock (_cacheLock)
				{
					if (_responseCache.TryGetValue(key, out CachedResponse? cached) && nowMs < cached._expiresMs)
					{
						status      = cached._status;
						contentType = cached._contentType;
						content     = cached._content;
						return true;
					}
					status      = 0;
					contentType = string.Empty;
					content     = Array.Empty<byte>();
					return false;
				}
			}

			// Overwrite whatever was cached for this key with the freshly produced answer -- IF it fits.  Two hard limits
			// keep the cache from being a memory-fill target: no single payload over kCacheMaxEntryBytes is ever cached
			// (a big file per unique URL must not become resident memory), and the whole cache never exceeds
			// kCacheMaxTotalBytes (when full, expired entries are pruned immediately; if still full, the answer is
			// served but not cached -- correctness never depends on caching).
			private void StoreCachedResponse(string key, long nowMs, long expiresMs, int status, string contentType, byte[] content)
			{
				if (content.Length > kCacheMaxEntryBytes)
					return;
				int footprint = EntryFootprint(key, content);
				lock (_cacheLock)
				{
					if (_responseCache.Remove(key, out CachedResponse? old))
						_cacheTotalBytes -= old._footprint;
					// BOTH bounds are enforced, because either one alone has a hole: bytes alone can't stop a flood of
					// empty responses under unique keys, and a count alone can't stop a few big ones.
					if (_cacheTotalBytes + footprint > kCacheMaxTotalBytes || _responseCache.Count >= kCacheMaxEntries)
						PruneExpiredLocked(nowMs); // make room out of whatever has expired before giving up
					if (_cacheTotalBytes + footprint > kCacheMaxTotalBytes || _responseCache.Count >= kCacheMaxEntries)
					{
						if (nowMs >= _nextCacheFullLogMs) // worth an operator's attention (endpoints losing their herd protection), but rate-limited
						{
							_nextCacheFullLogMs = nowMs + kCachePruneIntervalMs;
							_logger.Log(EVerbosity.Warning, $"Response cache is full ({_responseCache.Count}/{kCacheMaxEntries} entries, {_cacheTotalBytes}/{kCacheMaxTotalBytes} bytes); new responses are being served uncached.");
						}
						return;
					}
					_responseCache[key] = new CachedResponse() { _status = status, _contentType = contentType, _content = content, _expiresMs = expiresMs, _footprint = footprint };
					_cacheTotalBytes   += footprint;
				}
			}

			// Lazy policing: at most once a minute, sweep out entries whose time has passed.  Expiry correctness never
			// depends on this (the hit path checks _expiresMs); this only bounds memory for keys nobody asks for again.
			private void PruneCacheIfDue(long nowMs)
			{
				if (nowMs < Volatile.Read(ref _nextPruneMs))
					return; // fast path: no lock on the (vastly common) not-due case; the race just means two threads both enter and one finds nothing to prune
				lock (_cacheLock)
				{
					if (nowMs < _nextPruneMs)
						return;
					_nextPruneMs = nowMs + kCachePruneIntervalMs;
					PruneExpiredLocked(nowMs);
				}
			}

			// Any routing change invalidates the cache.  Cached bytes were produced by a SPECIFIC handler under a SPECIFIC
			// policy, and re-registering a path swaps both -- without this, the new handler's first callers keep getting
			// the old handler's answers until the TTL runs out, which for a route swapped to tighten access means serving
			// the pre-tightening body to people the new policy exists to exclude.  Cheap and blunt beats subtly stale.
			private void InvalidateCache()
			{
				lock (_cacheLock)
				{
					_responseCache.Clear();
					_cacheTotalBytes = 0;
				}
			}

			// Drop every expired entry and give its bytes back to the budget.  Caller MUST hold _cacheLock.
			private void PruneExpiredLocked(long nowMs)
			{
				List<string>? dead = null;
				foreach (KeyValuePair<string, CachedResponse> kvp in _responseCache)
				{
					if (nowMs >= kvp.Value._expiresMs)
					{
						dead ??= new List<string>();
						dead.Add(kvp.Key);
					}
				}
				if (dead != null)
				{
					foreach (string key in dead)
					{
						if (_responseCache.Remove(key, out CachedResponse? gone))
							_cacheTotalBytes -= gone._footprint;
					}
				}
			}
		}
	}
}