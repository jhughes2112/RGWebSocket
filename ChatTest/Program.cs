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

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Logging;

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
				private object _lock = new object();
				private int    _errors = 0;

				public EVerbosity Verbosity { get; set; }
				public int        ErrorCount => _errors;

				public ConsoleLogger(EVerbosity verbosity)
				{
					Verbosity = verbosity;
				}

				public void Log(EVerbosity level, string msg)
				{
					if (level==EVerbosity.Error)
						Interlocked.Increment(ref _errors);
					if (level>Verbosity)
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
					int clients = 24;
					int seed    = Environment.TickCount;
					int port    = 9696;
					int playMs  = 3500;
					EVerbosity verbosity = EVerbosity.Error;
					foreach (string arg in args)
					{
						string[] kv = arg.Split('=');
						if (kv.Length!=2) continue;
						switch (kv[0])
						{
							case "clients": clients = int.Parse(kv[1]); break;
							case "seed":    seed    = int.Parse(kv[1]); break;
							case "port":    port    = int.Parse(kv[1]); break;
							case "playms":  playMs  = int.Parse(kv[1]); break;
							case "verbose": verbosity = (EVerbosity)int.Parse(kv[1]); break;
						}
					}
					Console.WriteLine($"ChatTest starting: clients={clients} seed={seed} port={port} playms={playMs} verbose={verbosity}");
					Console.WriteLine($"(reproduce this exact run with: dotnet run --project ChatTest -- clients={clients} seed={seed} port={port} playms={playMs})");
					Console.WriteLine();

					ConsoleLogger logger = new ConsoleLogger(verbosity);
					PooledArray.Initialize(logger, 2000);  // warn if live buffer count runs away

					//-------------------
					// Server side.  The config deliberately tightens the library limits so the tests can trip them quickly:
					// 1MB inbound cap (phase 3 sends 2MB), 3s idle disconnect with a 1s sweep (phase 4 lurker goes silent).
					RGWebSocketConfig config = new RGWebSocketConfig()
					{
						MaxInboundMessageBytes = 1*1024*1024,
						MaxUnsentBytes         = 4*1024*1024,
						IdleDisconnectSeconds  = 3,
						IdleSweepPeriodSeconds = 1,
					};
					ChatConnectionManager mgr = new ChatConnectionManager(logger);
					WebServer server = new WebServer($"http://localhost:{port}/", 4, 5000, 30, mgr, logger, config);
					server.RegisterExactEndpoint("/status", (ctx) => Task.FromResult((200, "text/plain", System.Text.Encoding.UTF8.GetBytes($"connections={mgr.CurrentCount}"))));
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
					// Client herd.  Each gets its own deterministic RNG, a staggered start, and a slightly different play duration.
					Random master = new Random(seed);
					List<ChatClient> clientList = new List<ChatClient>();
					for (int i=0; i<clients; i++)
						clientList.Add(new ChatClient($"ws://localhost:{port}/", new Random(master.Next()), logger, RGWebSocketConfig.Default, startDelayMs: master.Next(0, 1500), playMs: playMs + master.Next(-1000, 1500)));

					Console.WriteLine($"Spawning {clients} clients...");
					long startedAt = Environment.TickCount64;
					List<Task> clientTasks = new List<Task>();
					foreach (ChatClient c in clientList)
						clientTasks.Add(Task.Run(c.Run));

					Task allClients = Task.WhenAll(clientTasks);
					bool stragglers = (await Task.WhenAny(allClients, Task.Delay(120000)).ConfigureAwait(false)) != allClients;
					if (stragglers)
						Console.WriteLine("FAIL: some clients did not finish within 120s (hung task?)");
					else
						Console.WriteLine($"All clients finished in {(Environment.TickCount64-startedAt)/1000.0:F1}s.");

					// Give the server a moment to reap the last disconnects, then it should be at zero connections.
					long deadline = Environment.TickCount64 + 8000;
					while (mgr.CurrentCount>0 && Environment.TickCount64<deadline)
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
					bool zombieDiscoed = false;
					long zombieDiscoMs = 0;
					int membersAtFloodStart = 0;
					using (ClientWebSocket zombie = new ClientWebSocket())
					{
						await zombie.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None).ConfigureAwait(false);
						byte[] iam = System.Text.Encoding.UTF8.GetBytes($"iam {Guid.NewGuid()}");
						await zombie.SendAsync(new ArraySegment<byte>(iam), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
						// ...and now the zombie never calls ReceiveAsync again.

						UnityWebSocket flooder = new UnityWebSocket(logger, "[flooder]", null, 5000, RGWebSocketConfig.Default);
						await flooder.Connect($"ws://localhost:{port}/", new Dictionary<string, string>()).ConfigureAwait(false);
						flooder.Send($"iam {Guid.NewGuid()}");
						await Task.Delay(250).ConfigureAwait(false);  // let both register as members
						membersAtFloodStart = mgr.CurrentCount;       // should be 2

						long floodStart = Environment.TickCount64;
						List<UnityWebSocket.wsMessage> floodInbox = new List<UnityWebSocket.wsMessage>();
						for (int i=0; i<100 && mgr.CurrentCount>1; i++)  // up to 100 x 512KB = 50MB before we give up
						{
							using (PooledArray blob = PooledArray.BorrowFromPool(512*1024))
							{
								blob.data[0] = 0xAB;  // keep the magic byte so nothing counts as corrupt
								flooder.Send(blob);
							}
							flooder.ReceiveAll(floodInbox);  // drain our own relayed copies so the FLOODER doesn't back up
							foreach (UnityWebSocket.wsMessage m in floodInbox)
								using (m.msg) { }
							floodInbox.Clear();
							await Task.Delay(25).ConfigureAwait(false);
						}
						long discoDeadline = Environment.TickCount64 + 10000;
						while (mgr.CurrentCount>1 && Environment.TickCount64<discoDeadline)
							await Task.Delay(50).ConfigureAwait(false);
						zombieDiscoed = (mgr.CurrentCount<=1);
						zombieDiscoMs = Environment.TickCount64 - floodStart;
						Console.WriteLine($"Phase 2: members at flood start={membersAtFloodStart}; zombie {(zombieDiscoed ? $"disconnected by server in {zombieDiscoMs}ms" : "NOT disconnected -- circuit breaker never tripped")}");

						flooder.Close();
						await Task.Delay(250).ConfigureAwait(false);
						flooder.Shutdown();
						flooder.ReceiveAll(floodInbox);  // release any relayed blobs that arrived during teardown
						foreach (UnityWebSocket.wsMessage m in floodInbox)
							using (m.msg) { }
						floodInbox.Clear();
					}
					{
						long p2deadline = Environment.TickCount64 + 5000;  // wait for the server to finish reaping both phase 2 sockets
						while (mgr.CurrentCount>0 && Environment.TickCount64<p2deadline)
							await Task.Delay(50).ConfigureAwait(false);
					}

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
						await Task.Delay(250).ConfigureAwait(false);  // let it register (1 member)
						try
						{
							byte[] huge = new byte[2*1024*1024];  // 2MB, double the configured 1MB inbound limit
							huge[0] = 0xAB;
							await bloater.SendAsync(new ArraySegment<byte>(huge), WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false);
						}
						catch (Exception)  // the server may reset the connection while we're mid-send; that IS the expected outcome
						{
						}
						long p3deadline = Environment.TickCount64 + 10000;
						while (mgr.CurrentCount>0 && Environment.TickCount64<p3deadline)
							await Task.Delay(50).ConfigureAwait(false);
						oversizeDiscoed = (mgr.CurrentCount==0);
						Console.WriteLine($"Phase 3: oversize sender {(oversizeDiscoed ? "disconnected by server" : "NOT disconnected -- inbound limit did not trip")}");
					}

					//-------------------
					// Phase 4: idle sweep.  A lurker connects, identifies, keeps READING (the TCP pipe is perfectly healthy), but never
					// sends again.  With IdleDisconnectSeconds=3 the sweep should disconnect it -- this is the half-open/dead-client
					// defense that transport-level idle timeouts fail to provide behind an Ingress.
					Console.WriteLine();
					Console.WriteLine("Phase 4: idle lurker (reads but never sends); the idle sweep should disconnect it...");
					bool lurkerSwept = false;
					long lurkerSweptMs = 0;
					using (ClientWebSocket lurker = new ClientWebSocket())
					{
						await lurker.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None).ConfigureAwait(false);
						byte[] lurkerIam = System.Text.Encoding.UTF8.GetBytes($"iam {Guid.NewGuid()}");
						await lurker.SendAsync(new ArraySegment<byte>(lurkerIam), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
						long lurkerStart = Environment.TickCount64;
						Task drainTask = Task.Run(async () =>  // stay alive and reading, completing the close handshake politely when it comes
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
							catch (Exception)  // aborts are also an acceptable way to die
							{
							}
						});
						long p4deadline = Environment.TickCount64 + 15000;
						while (mgr.CurrentCount>0 && Environment.TickCount64<p4deadline)
							await Task.Delay(100).ConfigureAwait(false);
						lurkerSwept = (mgr.CurrentCount==0);
						lurkerSweptMs = Environment.TickCount64 - lurkerStart;
						Console.WriteLine($"Phase 4: lurker {(lurkerSwept ? $"swept in {lurkerSweptMs}ms (idle limit 3s + sweep period 1s)" : "NOT swept -- idle sweep failed")}");
						await Task.WhenAny(drainTask, Task.Delay(3000)).ConfigureAwait(false);
					}

					//-------------------
					// Phase 5: shut the server down WHILE clients are still connected and chatting.  This exercises
					// ConnectionManager.Shutdown -> server-initiated close handshakes -> reaper drain, under load.
					Console.WriteLine();
					Console.WriteLine("Phase 5: spawning lingering clients, then shutting the server down underneath them...");
					List<ChatClient> lingerers = new List<ChatClient>();
					List<Task> lingerTasks = new List<Task>();
					for (int i=0; i<8; i++)
						lingerers.Add(new ChatClient($"ws://localhost:{port}/", new Random(master.Next()), logger, RGWebSocketConfig.Default, startDelayMs: 0, playMs: 60000));  // would chat for 60s if the server let them
					foreach (ChatClient c in lingerers)
						lingerTasks.Add(Task.Run(c.Run));
					await Task.Delay(2000).ConfigureAwait(false);  // let them all connect and get chatty
					int connectedBeforeShutdown = mgr.CurrentCount;

					long shutdownStart = Environment.TickCount64;
					await server.Shutdown().ConfigureAwait(false);
					long shutdownMs = Environment.TickCount64 - shutdownStart;

					Task allLingerers = Task.WhenAll(lingerTasks);
					bool lingerersHung = (await Task.WhenAny(allLingerers, Task.Delay(15000)).ConfigureAwait(false)) != allLingerers;
					int afterShutdownCount = mgr.CurrentCount;
					Console.WriteLine($"Phase 5: server shutdown took {shutdownMs}ms with {connectedBeforeShutdown} clients connected; clients {(lingerersHung ? "HUNG" : "all exited")}; server tracks {afterShutdownCount}.");
					clientList.AddRange(lingerers);  // fold their stats into the aggregate below

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
						sessions += c.Sessions;                 connectFailures += c.ConnectFailures;
						welcomes += c.Welcomes;                 listsSent += c.ListsSent;
						listsReceived += c.ListsReceived;       chatsReceived += c.ChatsReceived;
						broadcastsSent += c.BroadcastsSent;     whispersSent += c.WhispersSent;
						binariesSent += c.BinariesSent;         binariesReceived += c.BinariesReceived;
						binaryCorrupt += c.BinaryCorrupt;       gracefulCloses += c.GracefulCloses;
						abruptDeaths += c.AbruptDeaths;         reconnects += c.Reconnects;
						closeTimeouts += c.CloseTimeouts;       disconnectCallbacks += c.DisconnectCallbacks;
						serverErrors += c.ServerErrors;         unknownMsgs += c.UnknownMsgs;
						binaryBytes += c.BinaryBytesReceived;
						if (c.FatalError!=null)
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
					Console.WriteLine($"Logged Error lines:   {logger.ErrorCount} (abrupt deaths make some of these expected)");
					Console.WriteLine($"PooledArray live:     {liveAllocs} buffers / {Utilities.BytesToHumanReadable(liveAllocSize)} (expected: 1 buffer / 128B -- the close sentinel)");
					Console.WriteLine();

					//-------------------
					// Verdict.  Hard requirements: everyone finished, server tracking drained to zero, connections==disconnections,
					// no client fatals, no corrupt binaries, actual traffic flowed on every channel, and no pooled buffers leaked.
					List<string> failures = new List<string>();
					if (stragglers)                             failures.Add("clients hung past 120s");
					if (lingering>0)                            failures.Add($"server still tracks {lingering} connections");
					if (mgr.Connections!=mgr.Disconnections)    failures.Add($"connect/disconnect mismatch {mgr.Connections}!={mgr.Disconnections}");
					if (fatals>0)                               failures.Add($"{fatals} client(s) threw");
					if (binaryCorrupt>0)                        failures.Add($"{binaryCorrupt} corrupt binary payloads");
					if (unknownMsgs>0)                          failures.Add($"{unknownMsgs} unknown messages");
					if (sessions==0)                            failures.Add("no sessions ever connected");
					if (chatsReceived==0)                       failures.Add("no text chat was ever received");
					if (binariesReceived==0)                    failures.Add("no binary chat was ever received");
					if (listsReceived==0)                       failures.Add("no /list response was ever received");
					if (liveAllocs>1)                           failures.Add($"PooledArray leak: {liveAllocs} live buffers (expected 1)");
					if (welcomes!=sessions)                     failures.Add($"welcomes ({welcomes}) != sessions ({sessions}) -- lost or duplicated handshakes");
					if (closeTimeouts>0)                        failures.Add($"{closeTimeouts} graceful closes timed out");
					if (membersAtFloodStart!=2)                 failures.Add($"phase 2: expected zombie+flooder (2 members) at flood start, had {membersAtFloodStart}");
					if (zombieDiscoed==false)                   failures.Add("phase 2: slow consumer was never disconnected -- the unsent-bytes circuit breaker did not trip");
					if (oversizeDiscoed==false)                 failures.Add("phase 3: oversize sender was never disconnected -- the inbound message limit did not trip");
					if (lurkerSwept==false)                     failures.Add("phase 4: idle lurker was never disconnected -- the idle sweep did not work");
					if (connectedBeforeShutdown<8)              failures.Add($"phase 5 only had {connectedBeforeShutdown} clients connected at server shutdown (expected 8)");
					if (lingerersHung)                          failures.Add("phase 5 clients did not exit within 15s of server shutdown");
					if (afterShutdownCount>0)                   failures.Add($"phase 5: server still tracked {afterShutdownCount} connections after shutdown");
					if (shutdownMs>10000)                       failures.Add($"phase 5: server shutdown took {shutdownMs}ms (expected well under 10s)");

					if (failures.Count==0)
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
