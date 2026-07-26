//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// Stress-test orchestrator.  Boots a WebServer+chat connection manager, spawns a herd of ChatClients in-process,
// lets them chat/whisper/broadcast for a few seconds each with random lifecycles (graceful close, abrupt death,
// reconnect), then shuts everything down and verifies nothing is left behind.
//
//   dotnet run --project ChatTest -- clients=24 seed=12345 port=9696 playms=3500 verbose=0
//
// verbose: 0=Error 1=Warning 2=Info 3=Debug 4=Extreme

using Logging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		namespace ChatTest
		{
			// Simple console logger.  Note: RGWebSocket logs transport errors (aborted sockets etc.) at Error level,
			// and abrupt client deaths make some of those EXPECTED here, so we count them rather than fail on them.
			public class ConsoleLogger : ILogging
			{
				private object _lock   = new object();
				private int    _errors = 0;

				public EVerbosity Verbosity { get; set; }
				public int        ErrorCount => _errors;

				public ConsoleLogger(EVerbosity verbosity)
				{
					Verbosity = verbosity;
				}

				public void Log(EVerbosity level, string msg)
				{
					if (level == EVerbosity.Error)
						Interlocked.Increment(ref _errors);
					if (level > Verbosity)
						return;
					lock (_lock)
						Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}][{level}] {msg}");
				}

				public void Dispose()
				{
				}
			}

			public static class Program
			{
				public static async Task<int> Main(string[] args)
				{
					int        clients   = 24;
					int        seed      = Environment.TickCount;
					int        port      = 9696;
					int        playMs    = 3500;
					EVerbosity verbosity = EVerbosity.Error;
					foreach (string arg in args)
					{
						string[] kv = arg.Split('=');
						if (kv.Length != 2)
							continue;
						switch (kv[0])
						{
							case "clients":
								clients = int.Parse(kv[1]);
								break;
							case "seed":
								seed = int.Parse(kv[1]);
								break;
							case "port":
								port = int.Parse(kv[1]);
								break;
							case "playms":
								playMs = int.Parse(kv[1]);
								break;
							case "verbose":
								verbosity = (EVerbosity)int.Parse(kv[1]);
								break;
						}
					}
					Console.WriteLine($"ChatTest starting: clients={clients} seed={seed} port={port} playms={playMs} verbose={verbosity}");
					Console.WriteLine($"(reproduce this exact run with: dotnet run --project ChatTest -- clients={clients} seed={seed} port={port} playms={playMs})");
					Console.WriteLine();

					ConsoleLogger logger = new ConsoleLogger(verbosity);
					PooledArray.Initialize(logger, 2000); // warn if live buffer count runs away

					//-------------------
					// Server side.  Configure() deliberately tightens the library limits so the tests can trip them quickly:
					// 1MB inbound cap (phase 3 sends 2MB), 3s idle disconnect with a 1s sweep (phase 4 lurker goes silent).
					// Must happen before anything websocket-related is constructed, or Configure() throws.
					RGWebSocketConfig.Configure(receiveBufferBytes: 4096, maxInboundMessageBytes: 1 * 1024 * 1024, maxUnsentBytes: 4 * 1024 * 1024, maxRecvQueueBytes: 8 * 1024 * 1024, idleDisconnectSeconds: 3, idleSweepPeriodSeconds: 1, webSocketKeepAliveSeconds: 30, maxConcurrentWebSockets: 40); // phase 4.5 floods past the 40-socket cap
					ChatConnectionManager mgr            = new ChatConnectionManager(logger);
					TestDataCollection    dataCollection = new TestDataCollection(); // proves the IDataCollection conformance -- a real app would pass a prometheus-backed derivative
																				   // Pre-upgrade authorizer: rejects a hostile Origin BEFORE the handshake is accepted (phase 4.5 probes this).
																				   // Everything else (the in-process test clients send no Origin at all) is admitted.
					RGWebSocketServer.UpgradeAuthorizer wsAuth = (ctx, token) =>
					{
						if (ctx.Request.Headers["Origin"]=="http://evil.example")
							return Task.FromResult<(int, string, byte[])?>((403, "text/plain", System.Text.Encoding.UTF8.GetBytes("403 Forbidden")));
						return Task.FromResult<(int, string, byte[])?>(null);
					};
					RGWebServer server = new RGWebServer($"http://localhost:{port}/", 4, 5000, 30, mgr, logger, dataCollection, wsAuth);
					server.RegisterExactEndpoint("/status", (ctx, token) => Task.FromResult((200, "text/plain", System.Text.Encoding.UTF8.GetBytes($"connections={mgr.CurrentCount}"))), cacheSeconds: 0, cacheIgnoresQuery: false, authorizer: null);
					try
					{
						server.Start();
					}
					catch (Exception e)
					{
						Console.WriteLine($"FAIL: server could not start (port conflict or ACL issue?) {e.Message}");
						return 1;
					}

					using (HttpClient http = new HttpClient())
						Console.WriteLine($"HTTP GET /status -> \"{await http.GetStringAsync($"http://localhost:{port}/status").ConfigureAwait(false)}\"");

					//-------------------
					// Phase 0: HTTP hardening.  (a) Payloads over the cache's 100KB per-entry cap must never be cached, no
					// matter how many unique query strings ask for them (the classic memory-fill attack).  (b) An endpoint
					// registered with cacheIgnoresQuery collapses every query permutation to ONE cache entry serviced by ONE
					// handler run.  (c) A handler that overruns the connection timeout must produce an honest 503 and a
					// metrics count -- not a silent fake success.
					Console.WriteLine();
					Console.WriteLine("Phase 0: HTTP hardening (cache entry cap, query-stripped cache keys, honest handler timeouts)...");
					byte[] hugePage       = new byte[150*1024]; // over the 100KB per-entry cache cap
					byte[] smallPage      = System.Text.Encoding.UTF8.GetBytes("cached file bytes");
					int    bigHandlerRuns = 0, fileHandlerRuns = 0;
					server.RegisterExactEndpoint("/bigpage", (ctx, token) => { Interlocked.Increment(ref bigHandlerRuns); return Task.FromResult((200, "application/octet-stream", hugePage)); }, cacheSeconds: 60, cacheIgnoresQuery: false, authorizer: null);
					server.RegisterPrefixEndpoint("/files/", (ctx, token) => { Interlocked.Increment(ref fileHandlerRuns); return Task.FromResult((200, "text/plain", smallPage)); }, cacheSeconds: 60, cacheIgnoresQuery: true, authorizer: null);
					int  cacheEntriesAfterBig      = -1, cacheEntriesAfterFiles = -1, cacheEntriesAfterEmptyFlood = -1;
					long cacheBytesAfterEmptyFlood = -1;
					bool slowGot503                = false;
					long slowTimeouts              = -1;
					server.RegisterExactEndpoint("/empty", (ctx, token) => Task.FromResult((200, "text/plain", Array.Empty<byte>())), cacheSeconds: 60, cacheIgnoresQuery: false, authorizer: null);
					using (HttpClient http = new HttpClient())
					{
						for (int i = 0; i < 3; i++)
							await http.GetByteArrayAsync($"http://localhost:{port}/bigpage?attack={i}").ConfigureAwait(false);
						cacheEntriesAfterBig = server.CacheEntryCount; // oversized: nothing may have been cached
						for (int i = 0; i < 3; i++)
							await http.GetStringAsync($"http://localhost:{port}/files/readme.txt?attack={i}").ConfigureAwait(false);
						cacheEntriesAfterFiles = server.CacheEntryCount; // query-stripped: all three collapse to one entry

						// Zero-byte responses under unique query keys: the entry-count cap (10,000) is what stops this,
						// since a body-only budget would score every one of these as costing nothing.  Fire past the cap.
						for (int i = 0; i < 10200; i++)
							await http.GetByteArrayAsync($"http://localhost:{port}/empty?fill={i}").ConfigureAwait(false);
						cacheEntriesAfterEmptyFlood = server.CacheEntryCount;
						cacheBytesAfterEmptyFlood   = server.CacheTotalBytes;

						// The slow-handler test gets its own server with a short (750ms) timeout so it doesn't drag the run out.
						RGWebServer slowServer = new RGWebServer($"http://localhost:{port+2}/", 1, 750, 30, new ChatConnectionManager(logger), logger, null, null);
						slowServer.RegisterExactEndpoint("/slow", async (ctx, token) => { await Task.Delay(30000, token); return (200, "text/plain", smallPage); }, cacheSeconds: 0, cacheIgnoresQuery: false, authorizer: null);
						slowServer.Start();
						try
						{
							using (HttpResponseMessage slowResponse = await http.GetAsync($"http://localhost:{port + 2}/slow").ConfigureAwait(false))
								slowGot503 = ((int)slowResponse.StatusCode == 503);
						}
						catch (HttpRequestException) // an aborted connection would also not be a fake 200, but the expected path is a clean 503
						{
						}
						slowTimeouts = slowServer.Metrics.HttpHandlerTimeouts;
						await slowServer.Shutdown().ConfigureAwait(false);
					}
					Console.WriteLine($"Phase 0: bigpage cache entries={cacheEntriesAfterBig} (expect 0, {bigHandlerRuns} handler runs); files cache entries={cacheEntriesAfterFiles} (expect 1, {fileHandlerRuns} handler run); slow handler -> {(slowGot503 ? "503" : "NO 503")}, timeouts={slowTimeouts} (expect 1)");
					Console.WriteLine($"Phase 0: after 10200 empty-body unique-query requests: entries={cacheEntriesAfterEmptyFlood} (expect <=10000) footprint={Utilities.BytesToHumanReadable(cacheBytesAfterEmptyFlood)} (expect >0 -- keys and overhead are charged, not just bodies)");

					//-------------------
					// Client herd.  Each gets its own deterministic RNG, a staggered start, and a slightly different play duration.
					Random           master     = new Random(seed);
					List<ChatClient> clientList = new List<ChatClient>();
					for (int i = 0; i < clients; i++)
						clientList.Add(new ChatClient($"ws://localhost:{port}/", new Random(master.Next()), logger, startDelayMs: master.Next(0, 1500), playMs: playMs + master.Next(-1000, 1500)));

					Console.WriteLine($"Spawning {clients} clients...");
					long       startedAt   = Environment.TickCount64;
					List<Task> clientTasks = new List<Task>();
					foreach (ChatClient c in clientList)
						clientTasks.Add(Task.Run(c.Run));

					Task allClients = Task.WhenAll(clientTasks);
					bool stragglers = (await Task.WhenAny(allClients, Task.Delay(120000)).ConfigureAwait(false)) != allClients;
					if (stragglers)
						Console.WriteLine("FAIL: some clients did not finish within 120s (hung task?)");
					else
						Console.WriteLine($"All clients finished in {(Environment.TickCount64 - startedAt) / 1000.0:F1}s.");

					// Give the server a moment to reap the last disconnects, then it should be at zero connections.
					long deadline = Environment.TickCount64 + 8000;
					while (mgr.CurrentCount > 0 && Environment.TickCount64 < deadline)
						await Task.Delay(100).ConfigureAwait(false);
					int lingering = mgr.CurrentCount;

					using (HttpClient http = new HttpClient())
						Console.WriteLine($"HTTP GET /status -> \"{await http.GetStringAsync($"http://localhost:{port}/status").ConfigureAwait(false)}\"");

					//-------------------
					// Phase 2: slow-consumer circuit breaker.  A "zombie" client connects, identifies, then never reads another byte,
					// so its TCP window fills and the server's sends to it stall.  A flooder then broadcasts big binary blobs (relayed
					// to the zombie) until the zombie's server-side unsent queue blows past the library's limit and the server
					// disconnects it.  The leak check at the end proves the multi-megabyte abandoned queue was fully released.
					Console.WriteLine();
					Console.WriteLine("Phase 2: zombie client (never reads) + flooder; server should disconnect the zombie at the unsent-bytes limit...");
					bool zombieDiscoed       = false;
					long zombieDiscoMs       = 0;
					int  membersAtFloodStart = 0;
					using (ClientWebSocket zombie = new ClientWebSocket())
					{
						await zombie.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None).ConfigureAwait(false);
						byte[] iam = System.Text.Encoding.UTF8.GetBytes($"iam {Guid.NewGuid()}");
						await zombie.SendAsync(new ArraySegment<byte>(iam), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
						// ...and now the zombie never calls ReceiveAsync again.

						RGUnityWebSocket flooder = new RGUnityWebSocket(logger, "[flooder]", null, 5000);
						await flooder.Connect($"ws://localhost:{port}/", new Dictionary<string, string>()).ConfigureAwait(false);
						flooder.Send($"iam {Guid.NewGuid()}");
						await Task.Delay(250).ConfigureAwait(false); // let both register as members
						membersAtFloodStart = mgr.CurrentCount;      // should be 2

						long floodStart = Environment.TickCount64;
						List<RGUnityWebSocket.wsMessage> floodInbox = new List<RGUnityWebSocket.wsMessage>();
						for (int i = 0; i < 100 && mgr.CurrentCount > 1; i++) // up to 100 x 512KB = 50MB before we give up
						{
							using (PooledArray blob = PooledArray.BorrowFromPool(512 * 1024))
							{
								blob.data[0] = 0xAB; // keep the magic byte so nothing counts as corrupt
								flooder.Send(blob);
							}
							flooder.ReceiveAll(floodInbox); // drain our own relayed copies so the FLOODER doesn't back up
							foreach (RGUnityWebSocket.wsMessage m in floodInbox)
								using (m.msg)
								{ }
							floodInbox.Clear();
							await Task.Delay(25).ConfigureAwait(false);
						}
						long discoDeadline = Environment.TickCount64 + 10000;
						while (mgr.CurrentCount > 1 && Environment.TickCount64 < discoDeadline)
							await Task.Delay(50).ConfigureAwait(false);
						zombieDiscoed = (mgr.CurrentCount <= 1);
						zombieDiscoMs = Environment.TickCount64 - floodStart;
						Console.WriteLine($"Phase 2: members at flood start={membersAtFloodStart}; zombie {(zombieDiscoed ? $"disconnected by server in {zombieDiscoMs}ms" : "NOT disconnected -- circuit breaker never tripped")}");

						flooder.Close();
						await Task.Delay(250).ConfigureAwait(false);
						flooder.Shutdown();
						flooder.ReceiveAll(floodInbox); // release any relayed blobs that arrived during teardown
						foreach (RGUnityWebSocket.wsMessage m in floodInbox)
							using (m.msg)
							{ }
						floodInbox.Clear();
					}
					{
						long p2deadline = Environment.TickCount64 + 5000; // wait for the server to finish reaping both phase 2 sockets
						while (mgr.CurrentCount > 0 && Environment.TickCount64 < p2deadline)
							await Task.Delay(50).ConfigureAwait(false);
					}

					//-------------------
					// Phase 2.5: queue accounting must charge RETAINED memory, not payload length.  A 1-byte frame occupies
					// a whole pool bucket (4KB on the receive path), so charging its Length lets an attacker park
					// thousands of times more memory than any budget advertises -- and a zero-length frame charges
					// nothing at all, which is unbounded.  This probes the client's inbound budget (8MB) because it is
					// deterministic: the queue grows per MESSAGE with no TCP window in the way.  ~1900 one-byte frames
					// should trip 8MB when charging capacity; under payload charging, 1900 frames charge 1900 BYTES and
					// nothing ever trips.  The server's outbound breaker shares this fix (see RGWebSocket.QueueCharge).
					// The hoarder heartbeats so the 3s idle sweep can't take the credit for killing it.
					Console.WriteLine();
					Console.WriteLine("Phase 2.5: tiny-frame flood at a client that never drains; the budget must charge RETAINED bytes, not payload...");
					EDisconnectReason hoarderReason  = EDisconnectReason.None;
					int               tinyFramesSent = 0;
					{
						RGUnityWebSocket backlogger = new RGUnityWebSocket(logger, "[backlogger]", null, 5000);
						await backlogger.Connect($"ws://localhost:{port}/", new Dictionary<string, string>()).ConfigureAwait(false);
						backlogger.Send($"iam {Guid.NewGuid()}");
						// ...and it never calls ReceiveAll again, so everything relayed to it piles up in its inbound queue.

						RGUnityWebSocket tinyFlooder = new RGUnityWebSocket(logger, "[tinyflooder]", null, 5000);
						await tinyFlooder.Connect($"ws://localhost:{port}/", new Dictionary<string, string>()).ConfigureAwait(false);
						tinyFlooder.Send($"iam {Guid.NewGuid()}");
						await Task.Delay(250).ConfigureAwait(false); // let both register as members

						List<RGUnityWebSocket.wsMessage> tinyInbox = new List<RGUnityWebSocket.wsMessage>();
						for (int i = 0; i < 4000 && backlogger.IsConnected; i++)
						{
							using (PooledArray oneByte = PooledArray.BorrowFromPool(1))
							{
								oneByte.data[0] = 0xAB; // the magic byte, so nothing counts as corrupt
								tinyFlooder.Send(oneByte);
							}
							tinyFramesSent++;
							if ((i % 400) == 0)
								backlogger.Send("/list"); // heartbeat: keeps the server-side idle sweep off it, so a disconnect can only be the backlog breaker
							if ((i % 50) == 0)
							{
								tinyFlooder.ReceiveAll(tinyInbox); // the flooder DOES drain, so only the backlogger backs up
								foreach (RGUnityWebSocket.wsMessage m in tinyInbox)
									using (m.msg)
									{ }
								tinyInbox.Clear();
								await Task.Delay(1).ConfigureAwait(false);
							}
						}
						long tinyDeadline = Environment.TickCount64 + 5000;
						while (backlogger.IsConnected && Environment.TickCount64 < tinyDeadline)
							await Task.Delay(25).ConfigureAwait(false);
						hoarderReason = backlogger.DisconnectReason; // read BEFORE shutdown clears it

						await backlogger.ShutdownAsync().ConfigureAwait(false); // also proves the undrained queue is released (PooledArray check at the end)
						tinyFlooder.Close();
						await Task.Delay(250).ConfigureAwait(false);
						await tinyFlooder.ShutdownAsync().ConfigureAwait(false);
						tinyFlooder.ReceiveAll(tinyInbox);
						foreach (RGUnityWebSocket.wsMessage m in tinyInbox)
							using (m.msg)
							{ }
						tinyInbox.Clear();
					}
					{
						long p25deadline = Environment.TickCount64 + 5000;
						while (mgr.CurrentCount > 0 && Environment.TickCount64 < p25deadline)
							await Task.Delay(50).ConfigureAwait(false);
					}
					Console.WriteLine($"Phase 2.5: sent {tinyFramesSent} one-byte frames; non-draining client died of {hoarderReason} (expect InboundBacklog -- payload-length accounting would never trip)");

					//-------------------
					// Phase 3: inbound circuit breaker.  A client sends one 2MB message; the server's limit is configured at 1MB,
					// so it should be disconnected mid-accumulation and the partial message never dispatched.
					Console.WriteLine();
					Console.WriteLine("Phase 3: oversize sender; server should disconnect a client that exceeds MaxInboundMessageBytes...");
					bool oversizeDiscoed = false;
					using (ClientWebSocket bloater = new ClientWebSocket())
					{
						await bloater.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None).ConfigureAwait(false);
						byte[] bloaterIam = System.Text.Encoding.UTF8.GetBytes($"iam {Guid.NewGuid()}");
						await bloater.SendAsync(new ArraySegment<byte>(bloaterIam), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
						{
							long regDeadline = Environment.TickCount64 + 5000; // wait until the server actually REGISTERS it, otherwise the death-poll below can see count==0 before it ever hit 1
							while (mgr.CurrentCount < 1 && Environment.TickCount64 < regDeadline)
								await Task.Delay(25).ConfigureAwait(false);
						}
						try
						{
							byte[] huge = new byte[2*1024*1024]; // 2MB, double the configured 1MB inbound limit
							huge[0]     = 0xAB;
							await bloater.SendAsync(new ArraySegment<byte>(huge), WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false);
						}
						catch (Exception) // the server may reset the connection while we're mid-send; that IS the expected outcome
						{
						}
						long p3deadline = Environment.TickCount64 + 10000;
						while (mgr.CurrentCount > 0 && Environment.TickCount64 < p3deadline)
							await Task.Delay(50).ConfigureAwait(false);
						oversizeDiscoed = (mgr.CurrentCount == 0);
						Console.WriteLine($"Phase 3: oversize sender {(oversizeDiscoed ? "disconnected by server" : "NOT disconnected -- inbound limit did not trip")}");
					}

					//-------------------
					// Phase 4: idle sweep.  A lurker connects, identifies, keeps READING (the TCP pipe is perfectly healthy), but never
					// sends again.  With IdleDisconnectSeconds=3 the sweep should disconnect it -- this is the half-open/dead-client
					// defense that transport-level idle timeouts fail to provide behind an Ingress.
					Console.WriteLine();
					Console.WriteLine("Phase 4: idle lurker (reads but never sends); the idle sweep should disconnect it...");
					bool lurkerSwept   = false;
					long lurkerSweptMs = 0;
					using (ClientWebSocket lurker = new ClientWebSocket())
					{
						await lurker.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None).ConfigureAwait(false);
						byte[] lurkerIam = System.Text.Encoding.UTF8.GetBytes($"iam {Guid.NewGuid()}");
						await lurker.SendAsync(new ArraySegment<byte>(lurkerIam), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
						{
							long regDeadline = Environment.TickCount64 + 5000; // wait until the server actually REGISTERS it, otherwise the death-poll below can see count==0 before it ever hit 1
							while (mgr.CurrentCount < 1 && Environment.TickCount64 < regDeadline)
								await Task.Delay(25).ConfigureAwait(false);
						}
						long lurkerStart = Environment.TickCount64;
						Task drainTask   = Task.Run(async () => // stay alive and reading, completing the close handshake politely when it comes
						{
							byte[] buf = new byte[16*1024];
							try
							{
								while (true)
								{
									WebSocketReceiveResult r = await lurker.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).ConfigureAwait(false);
									if (r.MessageType==WebSocketMessageType.Close)
									{
										await lurker.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
										break;
									}
								}
							}
							catch (Exception) // aborts are also an acceptable way to die
							{
							}
						});
						long p4deadline = Environment.TickCount64 + 15000;
						while (mgr.CurrentCount > 0 && Environment.TickCount64 < p4deadline)
							await Task.Delay(100).ConfigureAwait(false);
						lurkerSwept   = (mgr.CurrentCount == 0);
						lurkerSweptMs = Environment.TickCount64 - lurkerStart;
						Console.WriteLine($"Phase 4: lurker {(lurkerSwept ? $"swept in {lurkerSweptMs}ms (idle limit 3s + sweep period 1s)" : "NOT swept -- idle sweep failed")}");
						await Task.WhenAny(drainTask, Task.Delay(3000)).ConfigureAwait(false);
					}

					//-------------------
					// Phase 4.5: connection-count breaker.  MaxConcurrentWebSockets is configured at 40; open 45 idle
					// sockets and the last 5 must be refused at the door (503 on the upgrade handshake), because
					// per-connection breakers cannot stop an attacker who simply opens MORE connections.
					Console.WriteLine();
					Console.WriteLine("Phase 4.5: websocket flood; upgrades past MaxConcurrentWebSockets (40) should be refused...");
					int capAccepted = 0, capRefused = 0;
					{
						List<ClientWebSocket> floodSockets = new List<ClientWebSocket>();
						for (int i = 0; i < 45; i++)
						{
							ClientWebSocket cws = new ClientWebSocket();
							try
							{
								await cws.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None).ConfigureAwait(false);
								capAccepted++;
								floodSockets.Add(cws);
							}
							catch (WebSocketException) // the 503 refusal surfaces as a failed upgrade handshake
							{
								capRefused++;
								cws.Dispose();
							}
						}
						foreach (ClientWebSocket cws in floodSockets)
						{
							cws.Abort(); // abrupt teardown on purpose; the server reaps them as TransportError
							cws.Dispose();
						}
						long capDeadline = Environment.TickCount64 + 10000;
						while (mgr.CurrentCount > 0 && Environment.TickCount64 < capDeadline)
							await Task.Delay(50).ConfigureAwait(false);
					}

					// ...and the pre-upgrade authorizer: a hostile Origin must be denied a 403 BEFORE the handshake,
					// never reaching the connection manager (cross-site websocket hijacking defense).
					bool evilOriginRejected = false;
					using (ClientWebSocket evil = new ClientWebSocket())
					{
						evil.Options.SetRequestHeader("Origin", "http://evil.example");
						try
						{
							await evil.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None).ConfigureAwait(false);
						}
						catch (WebSocketException) // the 403 denial surfaces as a failed upgrade handshake
						{
							evilOriginRejected = true;
						}
					}
					Console.WriteLine($"Phase 4.5: accepted={capAccepted} (expect 40) refused={capRefused} (expect 5); evil Origin {(evilOriginRejected ? "rejected pre-handshake" : "NOT rejected")}; server tracks {mgr.CurrentCount} after teardown");

					//-------------------
					// Phase 4.6: undelivered inbound messages must not survive a client teardown.  A client connects,
					// provokes traffic, and shuts down WITHOUT ever calling ReceiveAll -- so its inbound queue is full of
					// messages holding IncRef'd pooled buffers.  Those buffers must be released by Shutdown (or they leak
					// for the life of the process, which the end-of-run PooledArray check would catch), and the stale
					// messages must NOT be handed to the next session after a reconnect.
					Console.WriteLine();
					Console.WriteLine("Phase 4.6: client teardown with an undrained inbound queue (buffer release + no cross-session replay)...");
					int staleAfterReconnect = -1;
					{
						RGUnityWebSocket hoarder = new RGUnityWebSocket(logger, "[hoarder]", null, 5000);
						await hoarder.Connect($"ws://localhost:{port}/", new Dictionary<string, string>()).ConfigureAwait(false);
						hoarder.Send($"iam {Guid.NewGuid()}"); // the welcome + roster replies pile up...
						for (int i = 0; i < 10; i++)
							hoarder.Send("/list"); // ...and so do ten roster responses
						await Task.Delay(750).ConfigureAwait(false);         // let them all arrive and sit undrained
						await hoarder.ShutdownAsync().ConfigureAwait(false); // never called ReceiveAll: the queue is full and must be released here

						// Reconnect and immediately drain: anything that shows up came from the DEAD session.
						await hoarder.Connect($"ws://localhost:{port}/", new Dictionary<string, string>()).ConfigureAwait(false);
						List<RGUnityWebSocket.wsMessage> stale = new List<RGUnityWebSocket.wsMessage>();
						hoarder.ReceiveAll(stale);
						staleAfterReconnect = stale.Count;
						foreach (RGUnityWebSocket.wsMessage m in stale)
							using (m.msg)
							{ }
						await hoarder.ShutdownAsync().ConfigureAwait(false);
						long p46deadline = Environment.TickCount64 + 5000;
						while (mgr.CurrentCount > 0 && Environment.TickCount64 < p46deadline)
							await Task.Delay(50).ConfigureAwait(false);
					}
					Console.WriteLine($"Phase 4.6: messages replayed from the dead session after reconnect={staleAfterReconnect} (expect 0); leaked buffers show up in the PooledArray check below");

					//-------------------
					// Phase 5: shut the server down WHILE clients are still connected and chatting.  This exercises
					// ConnectionManager.Shutdown -> server-initiated close handshakes -> reaper drain, under load.
					Console.WriteLine();
					Console.WriteLine("Phase 5: spawning lingering clients, then shutting the server down underneath them...");
					List<ChatClient> lingerers   = new List<ChatClient>();
					List<Task>       lingerTasks = new List<Task>();
					for (int i = 0; i < 8; i++)
						lingerers.Add(new ChatClient($"ws://localhost:{port}/", new Random(master.Next()), logger, startDelayMs: 0, playMs: 60000)); // would chat for 60s if the server let them
					foreach (ChatClient c in lingerers)
						lingerTasks.Add(Task.Run(c.Run));
					await Task.Delay(2000).ConfigureAwait(false); // let them all connect and get chatty
					int connectedBeforeShutdown = mgr.CurrentCount;

					long shutdownStart = Environment.TickCount64;
					await server.Shutdown().ConfigureAwait(false);
					long shutdownMs = Environment.TickCount64 - shutdownStart;

					Task allLingerers       = Task.WhenAll(lingerTasks);
					bool lingerersHung      = (await Task.WhenAny(allLingerers, Task.Delay(15000)).ConfigureAwait(false)) != allLingerers;
					int  afterShutdownCount = mgr.CurrentCount;
					Console.WriteLine($"Phase 5: server shutdown took {shutdownMs}ms with {connectedBeforeShutdown} clients connected; clients {(lingerersHung ? "HUNG" : "all exited")}; server tracks {afterShutdownCount}.");
					clientList.AddRange(lingerers); // fold their stats into the aggregate below

					//-------------------
					// Phase 6: typed message layer.  The SAME message classes ride two swappable codecs (the point of
					// IMessageFactory owning both directions), plus garbage input must yield a ProtocolError disconnect.
					// Runs on port+1 with its own server, so it's independent of the (now stopped) main chat server.
					Console.WriteLine();
					Console.WriteLine("Phase 6: typed messages over two swappable codecs (packed binary + JSON)...");
					(bool typedBinaryOk, long typedProtocolErrors,  string typedBinDetail) = await TypedPhase.Run(port + 1, new PackedBinaryFactory(),  true, logger).ConfigureAwait(false);
					(  bool typedJsonOk,           long ignoredPE, string typedJsonDetail) = await TypedPhase.Run(port + 1,         new JsonFactory(), false, logger).ConfigureAwait(false);
					Console.WriteLine($"Phase 6: packedBinary {(typedBinaryOk ? "OK" : "FAILED")} ({typedBinDetail}); json {(typedJsonOk ? "OK" : "FAILED")} ({typedJsonDetail}); protocolErrors={typedProtocolErrors} (expected 1)");

					await Task.Delay(250).ConfigureAwait(false);
					GC.Collect();
					GC.WaitForPendingFinalizers();
					GC.Collect();
					long liveAllocs    = PooledArray.GetLiveAllocs();
					long liveAllocSize = PooledArray.GetLiveAllocSize();

					//-------------------
					// Aggregate the client reports.
					int sessions=0, connectFailures=0, welcomes=0, listsSent=0, listsReceived=0, chatsReceived=0;
					int broadcastsSent=0, whispersSent=0, binariesSent=0, binariesReceived=0, binaryCorrupt=0;
					int gracefulCloses=0, abruptDeaths=0, reconnects=0, closeTimeouts=0, disconnectCallbacks=0;
					int serverErrors=0, unknownMsgs=0, fatals=0;
					long binaryBytes=0;
					foreach (ChatClient c in clientList)
					{
						sessions            += c.Sessions;
						connectFailures     += c.ConnectFailures;
						welcomes            += c.Welcomes;
						listsSent           += c.ListsSent;
						listsReceived       += c.ListsReceived;
						chatsReceived       += c.ChatsReceived;
						broadcastsSent      += c.BroadcastsSent;
						whispersSent        += c.WhispersSent;
						binariesSent        += c.BinariesSent;
						binariesReceived    += c.BinariesReceived;
						binaryCorrupt       += c.BinaryCorrupt;
						gracefulCloses      += c.GracefulCloses;
						abruptDeaths        += c.AbruptDeaths;
						reconnects          += c.Reconnects;
						closeTimeouts       += c.CloseTimeouts;
						disconnectCallbacks += c.DisconnectCallbacks;
						serverErrors        += c.ServerErrors;
						unknownMsgs         += c.UnknownMsgs;
						binaryBytes         += c.BinaryBytesReceived;
						if (c.FatalError != null)
						{
							fatals++;
							Console.WriteLine($"CLIENT FATAL [{c.Id}]: {c.FatalError}");
						}
					}

					Console.WriteLine();
					Console.WriteLine("=============== RESULTS ===============");
					Console.WriteLine($"Client sessions:      {sessions} (from {clients} clients, {reconnects} reconnects)  connectFailures={connectFailures}");
					Console.WriteLine($"Lifecycle:            gracefulCloses={gracefulCloses} abruptDeaths={abruptDeaths} closeTimeouts={closeTimeouts} disconnectCallbacks={disconnectCallbacks}");
					Console.WriteLine($"Handshake:            welcomes={welcomes} (expect =={sessions})");
					Console.WriteLine($"Lists:                sent={listsSent} received={listsReceived}");
					Console.WriteLine($"Chat text:            broadcastsSent={broadcastsSent} whispersSent={whispersSent} chatsReceived={chatsReceived}");
					Console.WriteLine($"Chat binary:          sent={binariesSent} received={binariesReceived} ({Utilities.BytesToHumanReadable(binaryBytes)}) corrupt={binaryCorrupt}");
					Console.WriteLine($"Client-visible errs:  serverErrors={serverErrors} (whisper misses are normal) unknownMsgs={unknownMsgs} clientFatals={fatals}");
					Console.WriteLine(mgr.StatsString());
					Console.WriteLine("--------------- server distribution metrics ---------------");
					Console.WriteLine(server.Metrics.Report());
					Console.WriteLine("--------------- IDataCollection (prometheus sink) ----------");
					Console.WriteLine(System.Text.Encoding.UTF8.GetString(await dataCollection.Generate().ConfigureAwait(false)).TrimEnd());
					Console.WriteLine("------------------------------------------------------------");
					Console.WriteLine($"Logged Error lines:   {logger.ErrorCount} (abrupt deaths make some of these expected)");
					Console.WriteLine($"PooledArray live:     {liveAllocs} buffers / {Utilities.BytesToHumanReadable(liveAllocSize)} (expected: 1 buffer / 128B -- the close sentinel)");
					Console.WriteLine($"GC totals:            allocated={Utilities.BytesToHumanReadable(GC.GetTotalAllocatedBytes())} collections gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)}");
					Console.WriteLine();

					//-------------------
					// Verdict.  Hard requirements: everyone finished, server tracking drained to zero, connections==disconnections,
					// no client fatals, no corrupt binaries, actual traffic flowed on every channel, and no pooled buffers leaked.
					List<string> failures = new List<string>();
					if (stragglers)
						failures.Add("clients hung past 120s");
					if (lingering > 0)
						failures.Add($"server still tracks {lingering} connections");
					if (mgr.Connections != mgr.Disconnections)
						failures.Add($"connect/disconnect mismatch {mgr.Connections}!={mgr.Disconnections}");
					if (fatals > 0)
						failures.Add($"{fatals} client(s) threw");
					if (binaryCorrupt > 0)
						failures.Add($"{binaryCorrupt} corrupt binary payloads");
					if (unknownMsgs > 0)
						failures.Add($"{unknownMsgs} unknown messages");
					if (sessions == 0)
						failures.Add("no sessions ever connected");
					if (chatsReceived == 0)
						failures.Add("no text chat was ever received");
					if (binariesReceived == 0)
						failures.Add("no binary chat was ever received");
					if (listsReceived == 0)
						failures.Add("no /list response was ever received");
					if (liveAllocs > 1)
						failures.Add($"PooledArray leak: {liveAllocs} live buffers (expected 1)");
					if (welcomes != sessions)
						failures.Add($"welcomes ({welcomes}) != sessions ({sessions}) -- lost or duplicated handshakes");
					if (closeTimeouts > 0)
						failures.Add($"{closeTimeouts} graceful closes timed out");
					if (membersAtFloodStart != 2)
						failures.Add($"phase 2: expected zombie+flooder (2 members) at flood start, had {membersAtFloodStart}");
					if (zombieDiscoed == false)
						failures.Add("phase 2: slow consumer was never disconnected -- the unsent-bytes circuit breaker did not trip");
					if (oversizeDiscoed == false)
						failures.Add("phase 3: oversize sender was never disconnected -- the inbound message limit did not trip");
					if (lurkerSwept == false)
						failures.Add("phase 4: idle lurker was never disconnected -- the idle sweep did not work");
					if (cacheEntriesAfterBig != 0)
						failures.Add($"phase 0: {cacheEntriesAfterBig} oversized responses were cached -- the 100KB per-entry cap failed");
					if (bigHandlerRuns != 3)
						failures.Add($"phase 0: oversized endpoint ran {bigHandlerRuns} times for 3 unique queries (expected 3 -- nothing should have been served from cache)");
					if (cacheEntriesAfterFiles != 1)
						failures.Add($"phase 0: query-stripped endpoint left {cacheEntriesAfterFiles} cache entries for 3 query permutations (expected 1)");
					if (fileHandlerRuns != 1)
						failures.Add($"phase 0: query-stripped endpoint ran {fileHandlerRuns} times (expected 1 -- permutations 2 and 3 should have been cache hits)");
					if (slowGot503 == false)
						failures.Add("phase 0: slow handler did not produce an honest 503 on timeout");
					if (slowTimeouts != 1)
						failures.Add($"phase 0: expected exactly 1 http handler timeout in metrics, saw {slowTimeouts}");
					if (capAccepted != 40)
						failures.Add($"phase 4.5: {capAccepted} upgrades accepted (expected exactly 40 -- MaxConcurrentWebSockets)");
					if (capRefused != 5)
						failures.Add($"phase 4.5: {capRefused} upgrades refused (expected exactly 5)");
					if (evilOriginRejected == false)
						failures.Add("phase 4.5: hostile Origin was not rejected by the pre-upgrade authorizer");
					if (cacheEntriesAfterEmptyFlood > 10000)
						failures.Add($"phase 0: empty-response flood grew the cache to {cacheEntriesAfterEmptyFlood} entries (cap is 10000)");
					if (cacheBytesAfterEmptyFlood <= 0)
						failures.Add("phase 0: cache reports 0 bytes while holding entries -- keys/overhead are not being charged to the budget");
					if (staleAfterReconnect != 0)
						failures.Add($"phase 4.6: {staleAfterReconnect} message(s) from a dead session were replayed into the reconnected one");
					if (server.Metrics.RefusedUpgrades != 5)
						failures.Add($"metrics: refused upgrades {server.Metrics.RefusedUpgrades} != 5");
					if (dataCollection.GetCounter("rgws_upgrades_refused_total") != 5)
						failures.Add("IDataCollection: refused upgrades counter != 5");
					// The engineered kills must be attributed to the RIGHT cause, not just counted as generic deaths.
					if (hoarderReason != EDisconnectReason.InboundBacklog)
						failures.Add($"phase 2.5: non-draining client died of {hoarderReason}, expected InboundBacklog -- the queue budget is charging payload bytes instead of retained memory");
					if (server.Metrics.GetDisconnectCount(EDisconnectReason.OutboundBackpressure) != 1)
						failures.Add($"disconnect causes: expected exactly 1 OutboundBackpressure, saw {server.Metrics.GetDisconnectCount(EDisconnectReason.OutboundBackpressure)}");
					if (server.Metrics.GetDisconnectCount(EDisconnectReason.InboundOversize) != 1)
						failures.Add($"disconnect causes: expected exactly 1 InboundOversize, saw {server.Metrics.GetDisconnectCount(EDisconnectReason.InboundOversize)}");
					if (server.Metrics.GetDisconnectCount(EDisconnectReason.IdleTimeout) != 1)
						failures.Add($"disconnect causes: expected exactly 1 IdleTimeout, saw {server.Metrics.GetDisconnectCount(EDisconnectReason.IdleTimeout)}");
					if (typedBinaryOk == false)
						failures.Add($"phase 6: typed layer failed with the packed binary codec ({typedBinDetail})");
					if (typedJsonOk == false)
						failures.Add($"phase 6: typed layer failed with the JSON codec ({typedJsonDetail})");
					if (typedProtocolErrors != 1)
						failures.Add($"phase 6: expected exactly 1 ProtocolError disconnect from the garbage client, saw {typedProtocolErrors}");
					if (server.Metrics.HighWaterConnections < 8)
						failures.Add($"metrics: high water connections {server.Metrics.HighWaterConnections} is implausibly low");
					if (server.Metrics.CurrentConnections != 0)
						failures.Add($"metrics: current connections {server.Metrics.CurrentConnections} != 0 after shutdown");
					if (server.Metrics.CollectorFaults != 0)
						failures.Add($"metrics: the IDataCollection sink threw {server.Metrics.CollectorFaults} time(s)");
					// The IDataCollection sink must agree with the internal metrics -- this is the prometheus conformance check.
					if (dataCollection.GetCounter("rgws_disconnects_outbound_backpressure_total") != 1)
						failures.Add("IDataCollection: outbound backpressure counter != 1");
					if (dataCollection.GetCounter("rgws_disconnects_inbound_oversize_total") != 1)
						failures.Add("IDataCollection: inbound oversize counter != 1");
					if (dataCollection.GetCounter("rgws_disconnects_idle_timeout_total") != 1)
						failures.Add("IDataCollection: idle timeout counter != 1");
					if (dataCollection.GetCounter("rgws_connections_accepted_total") != server.Metrics.TotalAccepted)
						failures.Add("IDataCollection: accepted counter disagrees with internal metrics");
					if (dataCollection.GetGauge("rgws_connections_current") != 0)
						failures.Add("IDataCollection: current connections gauge != 0 after shutdown");
					if (dataCollection.GetGauge("rgws_connections_high_water") != server.Metrics.HighWaterConnections)
						failures.Add("IDataCollection: high water gauge disagrees with internal metrics");
					if (dataCollection.GetHistogramCount("rgws_inbound_message_bytes") != server.Metrics.InboundMsgBytes.Count)
						failures.Add("IDataCollection: inbound histogram count disagrees with internal metrics");
					if (dataCollection.GetHistogramCount("rgws_connection_duration_seconds") != server.Metrics.TotalAccepted)
						failures.Add("IDataCollection: duration histogram count != total accepted");
					if (connectedBeforeShutdown < 8)
						failures.Add($"phase 5 only had {connectedBeforeShutdown} clients connected at server shutdown (expected 8)");
					if (lingerersHung)
						failures.Add("phase 5 clients did not exit within 15s of server shutdown");
					if (afterShutdownCount > 0)
						failures.Add($"phase 5: server still tracked {afterShutdownCount} connections after shutdown");
					if (shutdownMs > 10000)
						failures.Add($"phase 5: server shutdown took {shutdownMs}ms (expected well under 10s)");

					if (failures.Count == 0)
					{
						Console.WriteLine("VERDICT: PASS");
						return 0;
					}
					Console.WriteLine($"VERDICT: FAIL ({failures.Count} problem(s))");
					foreach (string f in failures)
						Console.WriteLine($"  - {f}");
					return 1;
				}
			}
		}
	}
}