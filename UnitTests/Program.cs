//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// Unit + focused-integration test suite for RGWebSocket.  ChatTest remains the big concurrent stress gate;
// this suite pins down the exact contracts of every primitive and server behavior, one assertion at a time.
//
//   dotnet run --project UnitTests -- verbose=0
//
// Group ORDER is deliberate: RGWebSocketConfig tests must run before anything constructs a socket or server
// (construction freezes the config), and the frozen-config test runs at the end, after servers have existed.
// Exit code 0 = every test passed and no pooled buffer leaked.

using Logging;
using System;
using System.Threading.Tasks;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		namespace UnitTests
		{
			public static class Program
			{
				public static async Task<int> Main(string[] args)
				{
					EVerbosity verbosity = EVerbosity.Error;
					foreach (string arg in args)
					{
						string[] kv = arg.Split('=');
						if (kv.Length == 2 && kv[0] == "verbose")
							verbosity = (EVerbosity)int.Parse(kv[1]);
					}

					TestLogger logger = new TestLogger(verbosity);
					PooledArray.Initialize(logger, 100000);

					// Config tests FIRST -- Configure() is only legal before any socket/server exists.
					await ConfigTests.Run().ConfigureAwait(false);

					// The configuration the rest of the run executes under: library-ish defaults, breakers on, idle sweep off
					// (nothing here lurks long enough to test it -- ChatTest owns that), keepalives at the default.
					// The limits are sized for the brutality group: 8MB inbound so an oversize attack trips in bounded time,
					// 16MB outbound so a ~7.9MB legal echo fits but a slow-consumer flood still trips, and a 32MB client
					// backlog so one just-under-limit inbound message (charged at its 8MB bucket) never false-trips it.
					RGWebSocketConfig.Configure(receiveBufferBytes: 4096, maxInboundMessageBytes: 8 * 1024 * 1024, maxUnsentBytes: 16 * 1024 * 1024, maxRecvQueueBytes: 32 * 1024 * 1024, idleDisconnectSeconds: 0, idleSweepPeriodSeconds: 60, webSocketKeepAliveSeconds: 30, maxConcurrentWebSockets: 0);

					// Force the close-sentinel static to initialize NOW, so every later pool baseline is stable.
					_ = RGWebSocket.sCloseOutputAsync.Length;

					await UtilitiesTests.Run().ConfigureAwait(false);
					await LockingListTests.Run().ConfigureAwait(false);
					await ThreadSafeDictionaryTests.Run().ConfigureAwait(false);
					await ThreadSafeHashSetTests.Run().ConfigureAwait(false);
					await ChannelQueueTests.Run().ConfigureAwait(false);
					await PooledArrayTests.Run().ConfigureAwait(false);
					await TypedLayerTests.Run().ConfigureAwait(false);
					await MetricsTests.Run(logger).ConfigureAwait(false);
					await WebServerTests.Run(logger).ConfigureAwait(false);
					await SocketTests.Run(logger).ConfigureAwait(false);
					await ProtocolTests.Run(logger).ConfigureAwait(false);
					await BrutalityTests.Run(logger).ConfigureAwait(false);
					await ConfigTests.RunFrozen().ConfigureAwait(false);

					// Global leak check: after everything above, the ONLY live pooled buffer is the close sentinel.
					await Task.Delay(250).ConfigureAwait(false);
					GC.Collect();
					GC.WaitForPendingFinalizers();
					GC.Collect();
					long liveAllocs = PooledArray.GetLiveAllocs();

					Console.WriteLine();
					Console.WriteLine("=============== RESULTS ===============");
					Console.WriteLine($"Tests passed:       {Runner.Passed}");
					Console.WriteLine($"Tests failed:       {Runner.Failures.Count}");
					Console.WriteLine($"PooledArray live:   {liveAllocs} buffers / {Utilities.BytesToHumanReadable(PooledArray.GetLiveAllocSize())} (expected: 1 buffer -- the close sentinel)");
					Console.WriteLine($"Logged Error lines: {logger.ErrorCount} (breaker/violation tests make some of these expected)");
					Console.WriteLine($"GC totals:          allocated={Utilities.BytesToHumanReadable(GC.GetTotalAllocatedBytes())} collections gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)}");

					bool leaked = liveAllocs > 1;
					if (leaked)
						Console.WriteLine($"LEAK: {liveAllocs} pooled buffers still live (expected 1, the close sentinel)");

					if (Runner.Failures.Count == 0 && leaked == false)
					{
						Console.WriteLine("VERDICT: PASS");
						return 0;
					}
					Console.WriteLine($"VERDICT: FAIL ({Runner.Failures.Count} test failure(s){(leaked ? " + pooled buffer leak" : "")})");
					foreach (string f in Runner.Failures)
						Console.WriteLine($"  - {f}");
					return 1;
				}
			}
		}
	}
}