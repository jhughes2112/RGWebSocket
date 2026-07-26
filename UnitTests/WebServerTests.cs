//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// RGWebServer HTTP behavior against a REAL listener on localhost: routing precedence, the response cache's
// observable policies (TTL, GET-only, 200-only, invalidation on registration changes), per-endpoint
// authorization (including on cache hits -- the reason the authorizer lives in the server), and lifecycle reuse.

using Logging;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		namespace UnitTests
		{
			// Raw-mode manager that does nothing; these tests never open a websocket.
			public class NullManager : RGConnectionManager
			{
				public NullManager(ILogging logger) : base(logger)                               { }
				public override Task OnConnection(RGWebSocket rgws, HttpListenerContext context) { return Task.CompletedTask; }
				public override Task OnDisconnect(RGWebSocket rgws)                              { return Task.CompletedTask; }
				public override Task OnMessage   (RGWebSocket rgws, IRGMessage msg)              { return Task.CompletedTask; }
				public override Task Shutdown    () { return Task.CompletedTask; }
			}

			static public class WebServerTests
			{
				private const int kPort        = 9760;
				private const int kSubpathPort = 9761;

				private struct Reply
				{
					public int    Status;
					public string Body;
					public string Nosniff;
				}

				static private async Task<Reply> Get(HttpClient http, string url, string denyHeader = null)
				{
					using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url))
					{
						if (denyHeader != null)
							req.Headers.Add("X-Deny", denyHeader);
						using (HttpResponseMessage resp = await http.SendAsync(req).ConfigureAwait(false))
						{
							return new Reply()
							{
								Status  = (int)resp.StatusCode,
								Body    = await resp.Content.ReadAsStringAsync().ConfigureAwait(false),
								Nosniff = resp.Headers.TryGetValues("X-Content-Type-Options", out IEnumerable<string> v) ? string.Join(",", v) : "",
							};
						}
					}
				}

				static private (int, string, byte[]) Text(int status, string body)
				{
					return (status, "text/plain", System.Text.Encoding.UTF8.GetBytes(body));
				}

				static public async Task Run(TestLogger logger)
				{
					RGWebServer server = new RGWebServer($"http://localhost:{kPort}/", 2, 5000, 30, new NullManager(logger), logger, null, null);
					server.Start();
					using (HttpClient http = new HttpClient())
					{
						await Runner.Group("RGWebServer HTTP",
							("exact endpoint answers, with nosniff", async () =>
							{
								server.RegisterExactEndpoint("/hello", (ctx, token) => Task.FromResult(Text(200, "hi")), cacheSeconds: 0, cacheIgnoresQuery: false, authorizer: null);
								Reply r = await Get(http, $"http://localhost:{kPort}/hello").ConfigureAwait(false);
								Expect.Eq( 200, r.Status, "status");
								Expect.Eq("hi",   r.Body, "body");
								Expect.Eq("nosniff", r.Nosniff, "X-Content-Type-Options on every library-written response");
							}
						),
							("404 echoes the path but never the query", async () =>
							{
								Reply r = await Get(http, $"http://localhost:{kPort}/nowhere?token=SECRET123").ConfigureAwait(false);
								Expect.Eq(404, r.Status, "status");
								Expect.True(          r.Body.Contains("/nowhere"), "path is echoed for diagnosability");
								Expect.True(r.Body.Contains("SECRET123") == false, "query string must never be reflected into the response");
							}
						),
							("longest prefix wins regardless of registration order", async () =>
							{
								server.RegisterPrefixEndpoint("/api/",       (ctx, token) => Task.FromResult(Text(200, "public")), cacheSeconds: 0, cacheIgnoresQuery: false, authorizer: null);
								server.RegisterPrefixEndpoint("/api/admin/",  (ctx, token) => Task.FromResult(Text(200, "admin")), cacheSeconds: 0, cacheIgnoresQuery: false, authorizer: null);
								Reply broad  = await Get(http, $"http://localhost:{kPort}/api/other").ConfigureAwait(false);
								Reply narrow = await Get(http, $"http://localhost:{kPort}/api/admin/panel").ConfigureAwait(false);
								Expect.Eq("public", broad.Body, "broad prefix serves everything else");
								Expect.Eq("admin", narrow.Body, "narrow prefix wins even though the broad one was registered first (auth-bypass guard)");
							}
						),
							("exact match beats any prefix", async () =>
							{
								server.RegisterExactEndpoint("/api/exact", (ctx, token) => Task.FromResult(Text(200, "exact")), cacheSeconds: 0, cacheIgnoresQuery: false, authorizer: null);
								Reply r = await Get(http, $"http://localhost:{kPort}/api/exact").ConfigureAwait(false);
								Expect.Eq("exact", r.Body, "exact endpoint outranks the /api/ prefix");
							}
						),
							("cache: TTL serves from cache then re-runs the handler", async () =>
							{
								int runs = 0;
								server.RegisterExactEndpoint("/cached", (ctx, token) => { Interlocked.Increment(ref runs); return Task.FromResult(Text(200, "cached-body")); }, cacheSeconds: 1, cacheIgnoresQuery: false, authorizer: null);
								await Get(http, $"http://localhost:{kPort}/cached").ConfigureAwait(false);
								Reply hit = await Get(http, $"http://localhost:{kPort}/cached").ConfigureAwait(false);
								Expect.Eq(            1,     runs, "second request served from cache");
								Expect.Eq("cached-body", hit.Body, "cache hit body identical");
								await Task.Delay(1300).ConfigureAwait(false);
								await Get(http, $"http://localhost:{kPort}/cached").ConfigureAwait(false);
								Expect.Eq(2, runs, "expired entry re-runs the handler");
							}
						),
							("cache: POSTs are never cached and never served from cache", async () =>
							{
								int runs = 0;
								server.RegisterExactEndpoint("/action", (ctx, token) => { Interlocked.Increment(ref runs); return Task.FromResult(Text(200, "did-it")); }, cacheSeconds: 60, cacheIgnoresQuery: false, authorizer: null);
								using (HttpResponseMessage r1 = await http.PostAsync($"http://localhost:{kPort}/action", new ByteArrayContent(Array.Empty<byte>())).ConfigureAwait(false))
								using (HttpResponseMessage r2 = await http.PostAsync($"http://localhost:{kPort}/action", new ByteArrayContent(Array.Empty<byte>())).ConfigureAwait(false))
									Expect.Eq(2, runs, "every POST does the real work");
							}
						),
							("cache: non-200 answers are never cached", async () =>
							{
								int runs = 0;
								server.RegisterExactEndpoint("/flaky", (ctx, token) => { Interlocked.Increment(ref runs); return Task.FromResult(Text(500, "boom")); }, cacheSeconds: 60, cacheIgnoresQuery: false, authorizer: null);
								await Get(http, $"http://localhost:{kPort}/flaky").ConfigureAwait(false);
								await Get(http, $"http://localhost:{kPort}/flaky").ConfigureAwait(false);
								Expect.Eq(2, runs, "errors always retry the handler");
							}
						),
							("authorizer runs on EVERY request, including cache hits", async () =>
							{
								int                        runs = 0;
								RGWebServer.HTTPAuthorizer gate = (ctx, token) =>
								{
									if (ctx.Request.Headers["X-Deny"] == "1")
										return Task.FromResult<(int, string, byte[])?>(Text(403, "denied"));
									return Task.FromResult<(int, string, byte[])?>(null);
								};
								server.RegisterExactEndpoint("/guarded", (ctx, token) => { Interlocked.Increment(ref runs); return Task.FromResult(Text(200, "secret-page")); }, cacheSeconds: 60, cacheIgnoresQuery: false, authorizer: gate);
								Reply first = await Get(http, $"http://localhost:{kPort}/guarded").ConfigureAwait(false);
								Expect.Eq(200, first.Status, "admitted caller gets the page (now cached)");
								Reply denied = await Get(http, $"http://localhost:{kPort}/guarded", denyHeader: "1").ConfigureAwait(false);
								Expect.Eq(403, denied.Status, "denied caller is refused EVEN THOUGH the answer is sitting in cache");
								Expect.Eq("denied", denied.Body, "denial body served as-is");
								Reply second = await Get(http, $"http://localhost:{kPort}/guarded").ConfigureAwait(false);
								Expect.Eq(200, second.Status, "admitted caller still served");
								Expect.Eq(1, runs, "the 200s came from one handler run -- the denial was not cached over it");
							}
						),
							("a throwing authorizer fails CLOSED", async () =>
							{
								server.RegisterExactEndpoint("/brokenauth", (ctx, token) => Task.FromResult(Text(200, "should-never-be-seen")), cacheSeconds: 0, cacheIgnoresQuery: false,
									authorizer: (ctx, token) => throw new InvalidOperationException("auth backend down"));
								Reply r = await Get(http, $"http://localhost:{kPort}/brokenauth").ConfigureAwait(false);
								Expect.Eq(500, r.Status, "authorizer fault admits nobody");
								Expect.True(r.Body.Contains("should-never-be-seen") == false, "the handler never ran");
							}
						),
							("unregister 404s; re-register serves the NEW handler immediately (cache invalidated)", async () =>
							{
								server.RegisterExactEndpoint("/swap", (ctx, token) => Task.FromResult(Text(200, "old-content")), cacheSeconds: 60, cacheIgnoresQuery: false, authorizer: null);
								Reply oldReply = await Get(http, $"http://localhost:{kPort}/swap").ConfigureAwait(false);
								Expect.Eq("old-content", oldReply.Body, "old handler served and cached");
								server.UnregisterExactEndpoint("/swap");
								Reply gone = await Get(http, $"http://localhost:{kPort}/swap").ConfigureAwait(false);
								Expect.Eq(404, gone.Status, "unregistered endpoint is gone, not served from stale cache");
								server.RegisterExactEndpoint("/swap", (ctx, token) => Task.FromResult(Text(200, "new-content")), cacheSeconds: 60, cacheIgnoresQuery: false, authorizer: null);
								Reply fresh = await Get(http, $"http://localhost:{kPort}/swap").ConfigureAwait(false);
								Expect.Eq("new-content", fresh.Body, "re-registration must not serve the old handler's cached bytes");
							}
						),
							("cacheIgnoresQuery collapses query permutations to one entry", async () =>
							{
								int runs = 0;
								server.RegisterPrefixEndpoint("/static/", (ctx, token) => { Interlocked.Increment(ref runs); return Task.FromResult(Text(200, "file-bytes")); }, cacheSeconds: 60, cacheIgnoresQuery: true, authorizer: null);
								long entriesBefore = server.CacheEntryCount;
								for (int i = 0; i < 5; i++)
									await Get(http, $"http://localhost:{kPort}/static/app.js?v={i}").ConfigureAwait(false);
								Expect.Eq(                1,                         runs, "five query permutations, one handler run");
								Expect.Eq(entriesBefore + 1, (long)server.CacheEntryCount, "one cache entry, not five");
							}
						),
							("cache observability: entries carry a real footprint", () =>
							{
								Expect.True(server.CacheEntryCount > 0, "entries exist from the tests above");
								Expect.True(server.CacheTotalBytes > 0, "footprint is charged (bodies + keys + overhead)");
								return Task.CompletedTask;
							}
						),
							("Start while listening throws; Shutdown then Start serves again (reuse)", async () =>
							{
								Expect.Throws<InvalidOperationException>(() => server.Start(), "double Start");
								await server.Shutdown().ConfigureAwait(false);
								server.Start(); // StopListening closed the listener and completed the reap queue; StartListening must rebuild both
								Reply r = await Get(http, $"http://localhost:{kPort}/hello").ConfigureAwait(false);
								Expect.Eq( 200, r.Status, "reused server answers");
								Expect.Eq("hi",   r.Body, "handlers survived the stop/start cycle");
							}
						),
							("hosted at a subpath: prefix stripping and the root alias", async () =>
							{
								RGWebServer sub = new RGWebServer($"http://localhost:{kSubpathPort}/app/", 1, 5000, 30, new NullManager(logger), logger, null, null);
								sub.RegisterExactEndpoint("/status", (ctx, token) => Task.FromResult(Text(200, "sub-status")), cacheSeconds: 0, cacheIgnoresQuery: false, authorizer: null);
								sub.RegisterExactEndpoint("/",         (ctx, token) => Task.FromResult(Text(200, "sub-root")), cacheSeconds: 0, cacheIgnoresQuery: false, authorizer: null);
								sub.Start();
								try
								{
									Reply status = await Get(http, $"http://localhost:{kSubpathPort}/app/status").ConfigureAwait(false);
									Expect.Eq("sub-status", status.Body, "the hosting prefix is stripped before endpoint lookup");
									Reply root = await Get(http, $"http://localhost:{kSubpathPort}/app/").ConfigureAwait(false);
									Expect.Eq("sub-root", root.Body, "the hosting root maps to /");
								}
								finally
								{
									await sub.Shutdown().ConfigureAwait(false);
								}
							}
						)).ConfigureAwait(false);

						await server.Shutdown().ConfigureAwait(false);
					}
				}
			}
		}
	}
}