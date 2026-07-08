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
// verbose: 0=Error 1=Warning 2=Info 3=Debug 4=ExtremelyVerbose

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		namespace ChatTest
		{
			public static class Program
			{
				static private ELogVerboseType _verbosity = ELogVerboseType.Error;
				static private object _consoleLock = new object();
				static private int    _loggedErrors = 0;

				// Note: RGWebSocket logs transport errors (aborted sockets etc.) at Error level.  Abrupt client deaths make some of those EXPECTED here.
				static public void Log(ELogVerboseType level, string msg)
				{
					if (level==ELogVerboseType.Error)
						Interlocked.Increment(ref _loggedErrors);
					if (level>_verbosity)
						return;
					lock (_consoleLock)
						Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}][{level}] {msg}");
				}

				public static async Task<int> Main(string[] args)
				{
					int clients = 24;
					int seed    = Environment.TickCount;
					int port    = 9696;
					int playMs  = 3500;
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
							case "verbose": _verbosity = (ELogVerboseType)int.Parse(kv[1]); break;
						}
					}
					Console.WriteLine($"ChatTest starting: clients={clients} seed={seed} port={port} playms={playMs} verbose={_verbosity}");
					Console.WriteLine($"(reproduce this exact run with: dotnet run --project ChatTest -- clients={clients} seed={seed} port={port} playms={playMs})");
					Console.WriteLine();

					PooledArray.Initialize(Log, 2000);  // warn if live buffer count runs away

					//-------------------
					// Server side
					ChatConnectionManager mgr = new ChatConnectionManager(Log);
					WebServer server = new WebServer($"http://localhost:{port}/", 4, 5000, 30, mgr, Log);
					server.RegisterEndpoint("/status", (ctx) => Task.FromResult((200, "text/plain", System.Text.Encoding.UTF8.GetBytes($"connections={mgr.CurrentCount}"))));

					Task serverTask = server.Start();
					await Task.Delay(500).ConfigureAwait(false);
					if (serverTask.IsCompleted)  // WebServer.Start logs and returns on listener failure (e.g. port conflict) rather than throwing
					{
						Console.WriteLine("FAIL: server exited immediately on startup (port conflict or ACL issue?)");
						await serverTask.ConfigureAwait(false);
						return 1;
					}

					using (HttpClient http = new HttpClient())
						Console.WriteLine($"HTTP GET /status -> \"{await http.GetStringAsync($"http://localhost:{port}/status").ConfigureAwait(false)}\"");

					//-------------------
					// Client herd.  Each gets its own deterministic RNG, a staggered start, and a slightly different play duration.
					Random master = new Random(seed);
					List<ChatClient> clientList = new List<ChatClient>();
					for (int i=0; i<clients; i++)
						clientList.Add(new ChatClient($"ws://localhost:{port}/", new Random(master.Next()), Log, startDelayMs: master.Next(0, 1500), playMs: playMs + master.Next(-1000, 1500)));

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
					// Shut the server down and check for leaks.
					server.Shutdown();
					if ((await Task.WhenAny(serverTask, Task.Delay(10000)).ConfigureAwait(false)) != serverTask)
						Console.WriteLine("WARN: server did not stop within 10s of Shutdown()");

					await Task.Delay(250).ConfigureAwait(false);
					GC.Collect();
					GC.WaitForPendingFinalizers();
					GC.Collect();
					long liveAllocs   = PooledArray.GetLiveAllocs();
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
					Console.WriteLine($"Logged Error lines:   {_loggedErrors} (abrupt deaths make some of these expected)");
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
