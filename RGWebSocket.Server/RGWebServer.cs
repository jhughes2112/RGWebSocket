#nullable enable
﻿//-------------------
// Reachable Games
// Copyright 2023
//-------------------

using DataCollection;
using Logging;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		// Use this to easily register endpoints for callbacks for normal HTTP requests, whereas all websocket upgrades will be handled by the RGConnectionManager that is passed in.
		public class RGWebServer
		{
			private readonly string              _url;
			private readonly string              _urlPathPrefix;  // if this server is hosted at http://some.com/foo/bar, this is "/foo/bar/", for prefix-stripping request paths
			private readonly ILogging            _logger;
			private RGWebSocketServer            _httpServer;

			public WebSocketServerMetrics        Metrics => _httpServer.Metrics;  // distribution-oriented server metrics, updated live

			//-------------------

			// dataCollection is nullable ON PURPOSE: pass your IDataCollection derivative to feed prometheus, or null explicitly.
			public RGWebServer(string url, int listenerThreads, int connectionTimeoutMS, int idleSeconds, RGConnectionManager connectionManager, ILogging logger, IDataCollection? dataCollection)
			{
				_url                 = url;
				_logger              = logger;

				string[] urlParts = url.Split('/');  // When you have a url, you have protocol://domain:port/path/part/etc
				string urlPath    = string.Join('/', urlParts, 3, urlParts.Length-3);  // this leaves you with path/part/etc
				_urlPathPrefix    = "/" + urlPath;
				if (_urlPathPrefix.EndsWith("/", StringComparison.Ordinal)==false)
					_urlPathPrefix += "/";

				_httpServer = new RGWebSocketServer(listenerThreads, connectionTimeoutMS, idleSeconds, _url, HttpRequestHandler, connectionManager, _logger, dataCollection);
			}

			//-------------------

			public void Start()
			{
				if (_httpServer.IsListening())
					throw new InvalidOperationException($"WebServer.Start is already listening at {_url}");

				try
				{
					_httpServer.StartListening();  // start listening AFTER we have registered the handlers
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
				await _httpServer.StopListening().ConfigureAwait(false);  // kill all the connections and abort any that don't die quietly
				_logger.Log(EVerbosity.Warning, $"WebServer.Shutdown at {_url}");  // Warning matches Start's level -- a normal shutdown is noteworthy, not an error
			}

			//-------------------
			// HTTP handlers
			//-------------------
			// This is the set of http endpoint handlers are kept.  "/metrics" -> Metrics.HandleMetricsRequest, for example.
			public delegate Task<(int, string, byte[])> HTTPRequestHandler(HttpListenerContext context);  // handlers should return (httpStatus, contentType, content) so we can handle errors gracefully

			// Per-endpoint authorization, run by the server on EVERY request -- including ones answered from the
			// response cache, which is the whole point: a handler that checks auth internally never runs on a cache
			// hit, so a cached endpoint's gate MUST live here instead.  Return null to admit the request, or a
			// ready-to-send (status, contentType, body) denial.  Denials are never cached.
			public delegate Task<(int, string, byte[])?> HTTPAuthorizer(HttpListenerContext context);

			// Exact match endpoints - use dictionary for fast O(1) lookup
			private Dictionary<string, (HTTPRequestHandler handler, int cacheSeconds, HTTPAuthorizer? authorizer)> _exactEndpointHandlers = new Dictionary<string, (HTTPRequestHandler, int, HTTPAuthorizer?)>();

			// Prefix match endpoints - use list for ordered checking, stored as (prefix, handler, cacheSeconds, authorizer) tuples
			private List<(string prefix, HTTPRequestHandler handler, int cacheSeconds, HTTPAuthorizer? authorizer)> _prefixEndpointHandlers = new List<(string, HTTPRequestHandler, int, HTTPAuthorizer?)>();

			//-------------------
			// Outward-facing response cache: an endpoint registered with cacheSeconds > 0 has its successful (200) GET
			// responses cached by path+query for that many seconds, so a herd of identical requests costs ONE handler
			// invocation instead of N -- it behaves like a rate limiter on expensive public endpoints.  The cache is
			// lazy: expiry is checked on the hit path, and a once-a-minute prune sweeps out whatever went stale.
			// NEVER flag an endpoint whose response depends on WHO is asking (Authorization headers, cookies, roles) --
			// the cache would happily serve one caller's authorized answer to the next caller.  Non-GET requests and
			// non-200 responses are never cached, so errors and actions always do the real work.
			private class CachedResponse
			{
				public int    _status;
				public string _contentType = string.Empty;
				public byte[] _content = Array.Empty<byte>();
				public long   _expiresMs;
			}
			private readonly object _cacheLock = new object();
			private readonly Dictionary<string, CachedResponse> _responseCache = new Dictionary<string, CachedResponse>();
			private long _nextPruneMs;
			private const long kCachePruneIntervalMs = 60_000;

			// Register an endpoint that matches exactly.  BOTH policies are REQUIRED so every registration states them
			// explicitly: cacheSeconds 0 = never cached, N = successful GET responses are served from cache for N
			// seconds; authorizer null = public, non-null runs on EVERY request (cached or not) before anything is
			// served.  An endpoint whose RESPONSE varies by caller must still use cacheSeconds:0 -- the authorizer
			// makes gating cache-safe, but the cache still hands every admitted caller the same bytes.
			public void RegisterExactEndpoint(string urlPath, HTTPRequestHandler handler, int cacheSeconds, HTTPAuthorizer? authorizer)
			{
				if (_exactEndpointHandlers.TryAdd(urlPath, (handler, cacheSeconds, authorizer)) == false)
				{
					_logger.Log(EVerbosity.Error, $"RegisterExactEndpoint {urlPath} is already defined.  Ignoring.");
				}
			}

			// Register an endpoint that matches if the request path starts with the given prefix.  cacheSeconds and authorizer as above (required).
			public void RegisterPrefixEndpoint(string urlPrefix, HTTPRequestHandler handler, int cacheSeconds, HTTPAuthorizer? authorizer)
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

				_prefixEndpointHandlers.Add((urlPrefix, handler, cacheSeconds, authorizer));
			}

			// Unregister an exact endpoint
			public void UnregisterExactEndpoint(string urlPath)
			{
				if (_exactEndpointHandlers.Remove(urlPath) == false)
				{
					_logger.Log(EVerbosity.Error, $"UnregisterExactEndpoint {urlPath} not found to unregister.");
				}
			}

			// Unregister a prefix endpoint
			public void UnregisterPrefixEndpoint(string urlPrefix)
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

			// Regular HTTP calls come here.  They are dispatched to any registered endpoints.
			private async Task HttpRequestHandler(HttpListenerContext httpContext)
			{
				int     responseCode = 500;
				string  responseContentType = "text/plain";
				byte[]? responseContent = null;

				// Strip the hosting prefix off the FRONT of the path only.  (string.Replace would also mangle it if it appeared mid-path,
				// e.g. hosting at /api would break a request for /api/api-docs.)
				string path = httpContext.Request.Url?.AbsolutePath ?? string.Empty;
				string relativeEndpoint = path;
				if (_urlPathPrefix.Length>1)
				{
					if (path.StartsWith(_urlPathPrefix, StringComparison.Ordinal))
						relativeEndpoint = path.Substring(_urlPathPrefix.Length-1);  // keep the leading slash, e.g. /foo/metrics -> /metrics
					else if (path.Length==_urlPathPrefix.Length-1 && _urlPathPrefix.StartsWith(path, StringComparison.Ordinal))
						relativeEndpoint = "/";  // a request for the hosting root itself, without the trailing slash
				}
				
				HTTPRequestHandler? handler = null;
				HTTPAuthorizer? authorizer = null;
				int cacheSeconds = 0;

				// First, try exact match (fastest - O(1) dictionary lookup)
				if (_exactEndpointHandlers.TryGetValue(relativeEndpoint, out (HTTPRequestHandler handler, int cacheSeconds, HTTPAuthorizer? authorizer) exact))
				{
					handler = exact.handler;
					cacheSeconds = exact.cacheSeconds;
					authorizer = exact.authorizer;
				}
				else
				{
					// If no exact match, check prefix matches (slower - O(n) list iteration)
					foreach (var (prefix, prefixHandler, prefixCacheSeconds, prefixAuthorizer) in _prefixEndpointHandlers)
					{
						if (relativeEndpoint.StartsWith(prefix))
						{
							handler = prefixHandler;
							cacheSeconds = prefixCacheSeconds;
							authorizer = prefixAuthorizer;
							break; // Use the first matching prefix
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
							deny = await authorizer(httpContext).ConfigureAwait(false);
						}
						catch (Exception e)
						{
							_logger.Log(EVerbosity.Error, $"Exception in endpoint authorizer {httpContext.Request.Url?.ToString() ?? string.Empty} {e}");
							deny = (500, "text/plain", System.Text.Encoding.UTF8.GetBytes("500 Internal Server Error"));  // fail CLOSED: an authorizer that throws admits nobody
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
						// Cache key is path+query: /api/search?q=al and /api/search?q=bob are different answers.
						string cacheKey = httpContext.Request.Url?.PathAndQuery ?? relativeEndpoint;
						bool cacheable = cacheSeconds > 0 && httpContext.Request.HttpMethod=="GET";
						if (cacheable && TryGetCachedResponse(cacheKey, nowMs, out int cachedStatus, out string cachedType, out byte[] cachedContent))
						{
							responseCode = cachedStatus;
							responseContentType = cachedType;
							responseContent = cachedContent;
						}
						else
						{
							try
							{
								(responseCode, responseContentType, responseContent) = await handler(httpContext).ConfigureAwait(false);
								if (cacheable && responseCode==200 && responseContent!=null)
									StoreCachedResponse(cacheKey, nowMs + cacheSeconds*1000L, responseCode, responseContentType, responseContent);
							}
							catch (Exception e)
							{
								// Log the details, but never send exception text (stack frames, paths, internals) to whoever is on the other end of the socket.
								_logger.Log(EVerbosity.Error, $"Exception in endpoint handler {httpContext.Request.Url?.ToString() ?? string.Empty} {e}");
								responseCode = 500;
								responseContentType = "text/plain";
								responseContent = System.Text.Encoding.UTF8.GetBytes("500 Internal Server Error");
							}
						}
					}
				}
				else
				{
					responseCode = 404;
					responseContentType = "text/plain";
					responseContent = System.Text.Encoding.UTF8.GetBytes($"No endpoint found for {httpContext.Request.Url?.ToString() ?? string.Empty}");
				}

				try
				{
					httpContext.Response.ContentType = responseContentType;
					httpContext.Response.StatusCode = responseCode;
					if (responseContent!=null)
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
					_logger.Log(EVerbosity.Debug, $"Http response closed by client during write.  {httpContext.Request.Url?.ToString() ?? string.Empty} {e.GetType().Name}: {e.Message}");
				}
				catch (Exception e)
				{
					_logger.Log(EVerbosity.Error, $"Exception while trying to write to http response.  {httpContext.Request.Url?.ToString() ?? string.Empty} {e}");
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
						status = cached._status;
						contentType = cached._contentType;
						content = cached._content;
						return true;
					}
					status = 0;
					contentType = string.Empty;
					content = Array.Empty<byte>();
					return false;
				}
			}

			// Overwrite whatever was cached for this key with the freshly produced answer.
			private void StoreCachedResponse(string key, long expiresMs, int status, string contentType, byte[] content)
			{
				lock (_cacheLock)
				{
					_responseCache[key] = new CachedResponse() { _status = status, _contentType = contentType, _content = content, _expiresMs = expiresMs };
				}
			}

			// Lazy policing: at most once a minute, sweep out entries whose time has passed.  Expiry correctness never
			// depends on this (the hit path checks _expiresMs); this only bounds memory for keys nobody asks for again.
			private void PruneCacheIfDue(long nowMs)
			{
				lock (_cacheLock)
				{
					if (nowMs < _nextPruneMs)
						return;
					_nextPruneMs = nowMs + kCachePruneIntervalMs;
					List<string>? dead = null;
					foreach (KeyValuePair<string, CachedResponse> kvp in _responseCache)
					{
						if (nowMs >= kvp.Value._expiresMs)
						{
							dead ??= new List<string>();
							dead.Add(kvp.Key);
						}
					}
					if (dead!=null)
						foreach (string key in dead)
							_responseCache.Remove(key);
				}
			}
		}
	}
}