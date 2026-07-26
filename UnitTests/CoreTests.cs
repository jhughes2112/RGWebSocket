//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// Unit tests for the process-global config and the core primitives: Utilities, LockingList,
// ThreadSafeDictionary, ThreadSafeHashSet, and ChannelQueue.  Every shared structure also gets a
// concurrency hammer, because "works single-threaded" proves nothing about the job these do.

using Shared;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		namespace UnitTests
		{
			//-------------------
			// RGWebSocketConfig.  MUST run before anything constructs a socket/server (MarkInUse freezes the config),
			// which is why Program runs this group first.  The frozen-after-use case is tested at the END of the run.
			static public class ConfigTests
			{
				// Valid baseline with one knob perturbed per test.
				static private void Conf(int recv = 4096, int inbound = 1 << 20, int unsent = 4 << 20, int recvQ = 8 << 20, int idle = 0, int sweep = 60, int keepalive = 30, int maxConn = 0)
				{
					RGWebSocketConfig.Configure(recv, inbound, unsent, recvQ, idle, sweep, keepalive, maxConn);
				}

				static public Task Run()
				{
					return Runner.Group("RGWebSocketConfig",
						("defaults are the documented ones", () =>
						{
							Expect.Eq(            4096,        RGWebSocketConfig.ReceiveBufferBytes, "default ReceiveBufferBytes");
							Expect.Eq(16 * 1024 * 1024,    RGWebSocketConfig.MaxInboundMessageBytes, "default MaxInboundMessageBytes");
							Expect.Eq( 4 * 1024 * 1024,            RGWebSocketConfig.MaxUnsentBytes, "default MaxUnsentBytes");
							Expect.Eq( 8 * 1024 * 1024,         RGWebSocketConfig.MaxRecvQueueBytes, "default MaxRecvQueueBytes");
							Expect.Eq(               0,     RGWebSocketConfig.IdleDisconnectSeconds, "default IdleDisconnectSeconds");
							Expect.Eq(              60,    RGWebSocketConfig.IdleSweepPeriodSeconds, "default IdleSweepPeriodSeconds");
							Expect.Eq(              30, RGWebSocketConfig.WebSocketKeepAliveSeconds, "default WebSocketKeepAliveSeconds");
							Expect.Eq(               0,   RGWebSocketConfig.MaxConcurrentWebSockets, "default MaxConcurrentWebSockets");
							return Task.CompletedTask;
						}
					),
						("nonsense knobs each throw", () =>
						{
							Expect.Throws<ArgumentOutOfRangeException>(                   () => Conf(keepalive: -1), "negative keepalive");
							Expect.Throws<ArgumentOutOfRangeException>(                    () => Conf(keepalive: 3), "1-4s keepalive is a packet storm");
							Expect.Throws<ArgumentOutOfRangeException>(                       () => Conf(recvQ: -1), "negative recv queue budget");
							Expect.Throws<ArgumentOutOfRangeException>(                     () => Conf(maxConn: -1), "negative connection cap");
							Expect.Throws<ArgumentOutOfRangeException>(                       () => Conf(recv: 127), "recv buffer under the 128B minimum bucket");
							Expect.Throws<ArgumentOutOfRangeException>(() => Conf(recv: PooledArray.kMaxLength + 1), "recv buffer past the pool maximum");
							Expect.Throws<ArgumentOutOfRangeException>(       () => Conf(recv: 4096, inbound: 4095), "inbound cap below recv buffer");
							Expect.Throws<ArgumentOutOfRangeException>(                       () => Conf(unsent: 0), "zero unsent budget disconnects everyone");
							Expect.Throws<ArgumentOutOfRangeException>(                        () => Conf(idle: -1), "negative idle seconds");
							Expect.Throws<ArgumentOutOfRangeException>(() => Conf(idle: 5, sweep: 0), "idle sweep enabled with zero period is a busy-loop");
							return Task.CompletedTask;
						}
					),
						("a failed Configure mutates nothing", () =>
						{
							Conf(recv: 8192, inbound: 2 << 20, keepalive: 20);
							Expect.Throws<ArgumentOutOfRangeException>(() => Conf(recv: 100, inbound: 3 << 20, keepalive: 45), "bad recv buffer");
							Expect.Eq(   8192,        RGWebSocketConfig.ReceiveBufferBytes, "recv buffer untouched by the failed call");
							Expect.Eq(2 << 20,    RGWebSocketConfig.MaxInboundMessageBytes, "inbound cap untouched by the failed call");
							Expect.Eq(     20, RGWebSocketConfig.WebSocketKeepAliveSeconds, "keepalive untouched by the failed call");
							return Task.CompletedTask;
						}
					),
						("a valid Configure sets every knob", () =>
						{
							Conf(recv: 2048, inbound: 1 << 20, unsent: 3 << 20, recvQ: 5 << 20, idle: 7, sweep: 2, keepalive: 15, maxConn: 99);
							Expect.Eq(   2048,        RGWebSocketConfig.ReceiveBufferBytes, "ReceiveBufferBytes");
							Expect.Eq(1 << 20,    RGWebSocketConfig.MaxInboundMessageBytes, "MaxInboundMessageBytes");
							Expect.Eq(3 << 20,            RGWebSocketConfig.MaxUnsentBytes, "MaxUnsentBytes");
							Expect.Eq(5 << 20,         RGWebSocketConfig.MaxRecvQueueBytes, "MaxRecvQueueBytes");
							Expect.Eq(      7,     RGWebSocketConfig.IdleDisconnectSeconds, "IdleDisconnectSeconds");
							Expect.Eq(      2,    RGWebSocketConfig.IdleSweepPeriodSeconds, "IdleSweepPeriodSeconds");
							Expect.Eq(     15, RGWebSocketConfig.WebSocketKeepAliveSeconds, "WebSocketKeepAliveSeconds");
							Expect.Eq(     99,   RGWebSocketConfig.MaxConcurrentWebSockets, "MaxConcurrentWebSockets");
							return Task.CompletedTask;
						}
					),
						("keepalive interval maps 0 to Infinite", () =>
						{
							Conf(keepalive: 0);
							Expect.Eq(Timeout.InfiniteTimeSpan, RGWebSocketConfig.WebSocketKeepAliveInterval, "0 disables keepalive");
							Conf(keepalive: 30);
							Expect.Eq(TimeSpan.FromSeconds(30), RGWebSocketConfig.WebSocketKeepAliveInterval, "30s keepalive");
							return Task.CompletedTask;
						}
					));
				}

				// Called by Program at the very END of the run, after sockets/servers have existed.
				static public Task RunFrozen()
				{
					return Runner.Group("RGWebSocketConfig (frozen)",
						("Configure throws once anything is in use", () =>
						{
							Expect.Throws<InvalidOperationException>(() => Conf(), "config must be frozen after the first socket/server exists");
							return Task.CompletedTask;
						}
					));
				}
			}

			//-------------------
			static public class UtilitiesTests
			{
				// The formatter uses the current culture ("F1"), so expected strings are built the same way
				// instead of hardcoding a decimal point the test machine may not use.
				static private string F1(double v) { return v.ToString("F1"); }

				static public Task Run()
				{
					return Runner.Group("Utilities",
						("BytesToHumanReadable boundaries", () =>
						{
							Expect.Eq("0B",                       Utilities.BytesToHumanReadable(0), "zero");
							Expect.Eq($"{F1(1)}B",                Utilities.BytesToHumanReadable(1), "one byte");
							Expect.Eq($"{F1(1023)}B",          Utilities.BytesToHumanReadable(1023), "just under 1KB");
							Expect.Eq($"{F1(1)}KB",            Utilities.BytesToHumanReadable(1024), "exactly 1KB");
							Expect.Eq($"{F1(1.5)}KB",          Utilities.BytesToHumanReadable(1536), "1.5KB");
							Expect.Eq($"{F1(1)}MB",     Utilities.BytesToHumanReadable(1024 * 1024), "exactly 1MB");
							Expect.Eq($"{F1(8)}EB",   Utilities.BytesToHumanReadable(long.MaxValue), "long.MaxValue");
							return Task.CompletedTask;
						}
					),
						("BytesToHumanReadable negatives (including long.MinValue)", () =>
						{
							Expect.Eq($"{F1(-1)}KB", Utilities.BytesToHumanReadable(-1024), "negative 1KB");
							Expect.Eq($"{F1(-8)}EB", Utilities.BytesToHumanReadable(long.MinValue), "long.MinValue must not overflow in Math.Abs");
							return Task.CompletedTask;
						}
					));
				}
			}

			//-------------------
			static public class LockingListTests
			{
				static public Task Run()
				{
					return Runner.Group("LockingList",
						("basic ops and ordering", () =>
						{
							LockingList<string> list = new LockingList<string>();
							Expect.Eq(   0,      list.Count, "empty count");
							Expect.Eq(null, list.PopFront(), "PopFront on empty returns default");
							Expect.Eq(null,  list.PopBack(), "PopBack on empty returns default");
							list.Add("a");
							list.Add("b");
							list.Add("c");
							Expect.Eq(    3,       list.Count, "count after adds");
							Expect.Eq(  "a",  list.PopFront(), "PopFront is FIFO end");
							Expect.Eq(  "c",   list.PopBack(), "PopBack is LIFO end");
							Expect.Eq( true, list.Remove("b"), "Remove present");
							Expect.Eq(false, list.Remove("b"), "Remove absent");
							Expect.Eq(    0,       list.Count, "drained");
							return Task.CompletedTask;
						}
					),
						("MoveTo appends and clears; DoForEach passes state without a closure", () =>
						{
							LockingList<int> list = new LockingList<int>();
							for (int i = 0; i < 5; i++)
								list.Add(i);
							List<int> dest = new List<int>() { 100 };
							list.MoveTo(dest);
							Expect.Eq(6, dest.Count, "MoveTo appends to existing contents");
							Expect.Eq(0, list.Count, "MoveTo clears the source");
							Expect.Eq(4,    dest[5], "order preserved");
							list.Add(10);
							list.Add(20);
							int[] sum = new int[1];
							list.DoForEach((v, state) => ((int[])state)[0] += v, sum);
							Expect.Eq(30, sum[0], "DoForEach visited every element with the state object");
							list.Clear();
							Expect.Eq(0, list.Count, "Clear");
							return Task.CompletedTask;
						}
					),
						("concurrent adds and drains lose nothing", async () =>
						{
							LockingList<int> list = new LockingList<int>();
							const int kTasks = 8, kPer = 20000;
							Task[] producers = new Task[kTasks];
							for (int t = 0; t < kTasks; t++)
								producers[t] = Task.Run(() => { for (int i = 0; i < kPer; i++) list.Add(1); });
							List<int> drained = new List<int>();
							Task      all     = Task.WhenAll(producers);
							while (all.IsCompleted == false)
								list.MoveTo(drained);
							await all.ConfigureAwait(false);
							list.MoveTo(drained);
							Expect.Eq(kTasks * kPer, drained.Count, "every concurrent Add must be drained exactly once");
							Expect.Eq(            0,    list.Count, "list empty after final drain");
						}
					));
				}
			}

			//-------------------
			static public class ThreadSafeDictionaryTests
			{
				static public Task Run()
				{
					return Runner.Group("ThreadSafeDictionary",
						("basic ops", () =>
						{
							using (ThreadSafeDictionary<string, string> d = new ThreadSafeDictionary<string, string>())
							{
								Expect.Eq( true, d.Add("k", "v1"), "first Add");
								Expect.Eq(false, d.Add("k", "v2"), "duplicate Add refused");
								Expect.Eq(true, d.TryGetValue("k", out string v) && v == "v1", "duplicate Add did not overwrite");
								d.AddOrUpdate("k", "v3");
								Expect.Eq( true, d.TryGetValue("k", out v) && v == "v3", "AddOrUpdate overwrites");
								Expect.Eq( true,                     d.ContainsKey("k"), "ContainsKey present");
								Expect.Eq(false,                     d.ContainsKey("x"), "ContainsKey absent");
								Expect.Eq( true,   d.TryRemove("k", out v) && v == "v3", "TryRemove returns the removed value");
								Expect.Eq(false, d.Remove("k"), "Remove absent");
								Expect.Eq(    0,       d.Count, "empty");
								d.Add("a", "1");
								d.Add("b", "2");
								int visited = 0;
								d.Foreach((k, val) => visited++);
								Expect.Eq(2, visited, "Foreach visits everything");
								d.Clear();
								Expect.Eq(0, d.Count, "Clear");
							}
							return Task.CompletedTask;
						}
					),
						("RemoveIf checks the predicate under the write lock", () =>
						{
							using (ThreadSafeDictionary<string, string> d = new ThreadSafeDictionary<string, string>())
							{
								d.Add("k", "old");
								Expect.Eq(false, d.RemoveIf("k", v => v == "new"), "predicate false leaves the entry");
								Expect.Eq( true,               d.ContainsKey("k"), "still present");
								Expect.Eq( true, d.RemoveIf("k", v => v == "old"), "predicate true removes");
								Expect.Eq(false, d.RemoveIf("k", v => true), "absent key is false regardless of predicate");
							}
							return Task.CompletedTask;
						}
					),
						("GetOrAdd constructs exactly once under contention", async () =>
						{
							using (ThreadSafeDictionary<int, object> d = new ThreadSafeDictionary<int, object>())
							{
								int      constructions = 0;
								object[] results       = new object[16];
								Task[]   tasks         = new Task[16];
								using (ManualResetEventSlim go = new ManualResetEventSlim(false))
								{
									for (int t = 0; t < tasks.Length; t++)
									{
										int slot = t;
										tasks[t] = Task.Run(() =>
										{
											go.Wait();
											results[slot] = d.GetOrAdd(1, () => { Interlocked.Increment(ref constructions); return new object(); });
										});
									}
									go.Set();
									await Task.WhenAll(tasks).ConfigureAwait(false);
								}
								Expect.Eq(1, constructions, "the whole point of construct-under-lock: exactly one construction");
								for (int t = 1; t < results.Length; t++)
									Expect.True(ReferenceEquals(results[0], results[t]), "every caller got the same instance");
							}
						}
					),
						("GetOrAdd refuses a null from the callback", () =>
						{
							using (ThreadSafeDictionary<int, object> d = new ThreadSafeDictionary<int, object>())
							{
								Expect.Throws<InvalidOperationException>(() => d.GetOrAdd(1, () => null), "null construction must fail fast, not poison the dictionary");
								Expect.Eq(false, d.ContainsKey(1), "the poisoned key was not stored");
							}
							return Task.CompletedTask;
						}
					));
				}
			}

			//-------------------
			static public class ThreadSafeHashSetTests
			{
				static public Task Run()
				{
					return Runner.Group("ThreadSafeHashSet",
						("basic ops", () =>
						{
							using (ThreadSafeHashSet<int> s = new ThreadSafeHashSet<int>())
							{
								Expect.Eq( true,      s.Add(1), "first Add");
								Expect.Eq(false,      s.Add(1), "duplicate Add refused");
								Expect.Eq( true, s.Contains(1), "Contains present");
								Expect.Eq(false, s.Contains(2), "Contains absent");
								Expect.Eq( true,   s.Remove(1), "Remove present");
								Expect.Eq(false,   s.Remove(1), "Remove absent");
								s.Add(1);
								s.Add(2);
								int sum = 0;
								s.Foreach(k => sum += k);
								Expect.Eq(3, sum, "Foreach visits everything");
								s.Clear();
								Expect.Eq(0, s.Count, "Clear");
							}
							return Task.CompletedTask;
						}
					),
						("TryAddBelow enforces the cap atomically under contention", async () =>
						{
							using (ThreadSafeHashSet<int> s = new ThreadSafeHashSet<int>())
							{
								const int kCap = 50, kAttempts = 400;
								int    admitted = 0;
								Task[] tasks    = new Task[8];
								for (int t = 0; t < tasks.Length; t++)
								{
									int baseKey = t * kAttempts;
									tasks[t]    = Task.Run(() =>
									{
										for (int i = 0; i < kAttempts; i++)
											if (s.TryAddBelow(baseKey + i, kCap))
												Interlocked.Increment(ref admitted);
									});
								}
								await Task.WhenAll(tasks).ConfigureAwait(false);
								Expect.Eq(kCap, admitted, "exactly cap admissions, never one more (the race a Count-then-Add would lose)");
								Expect.Eq(kCap, s.Count, "set size equals the cap");
							}
						}
					));
				}
			}

			//-------------------
			static public class ChannelQueueTests
			{
				static public Task Run()
				{
					return Runner.Group("ChannelQueue",
						("FIFO order, Count, MoveTo", () =>
						{
							ChannelQueue<int> q = new ChannelQueue<int>(singleReader: true, singleWriter: true);
							for (int i = 0; i < 1000; i++)
								Expect.Eq(true, q.Add(i), "Add on an open queue");
							Expect.Eq(1000, q.Count, "Count tracks adds (Reader.Count would throw on this channel)");
							List<int> drained = new List<int>();
							q.MoveTo(drained);
							Expect.Eq(1000, drained.Count, "everything drained");
							Expect.Eq(   0,       q.Count, "Count tracks drains");
							for (int i = 0; i < 1000; i++)
								if (drained[i] != i)
									throw new Expect.TestFailure($"FIFO order broken at {i}: got {drained[i]}");
							return Task.CompletedTask;
						}
					),
						("Complete refuses later Adds but drains earlier ones", () =>
						{
							ChannelQueue<int> q = new ChannelQueue<int>(singleReader: true, singleWriter: false);
							q.Add(1);
							q.Add(2);
							q.Complete();
							Expect.Eq(false, q.Add(3), "Add after Complete must be refused, not silently swallowed");
							List<int> drained = new List<int>();
							q.MoveTo(drained);
							Expect.Eq(2, drained.Count, "items queued before Complete are still drainable");
							return Task.CompletedTask;
						}
					),
						("WaitToReadAsync wakes on Add", async () =>
						{
							ChannelQueue<int> q    = new ChannelQueue<int>(singleReader: true, singleWriter: true);
							Task              wait = q.WaitToReadAsync(CancellationToken.None).AsTask();
							q.Add(42);
							Task first = await Task.WhenAny(wait, Task.Delay(2000)).ConfigureAwait(false);
							Expect.True(first == wait, "waiter must wake when an item arrives");
						}
					),
						("WaitToReadAsync honors cancellation", async () =>
						{
							ChannelQueue<int> q = new ChannelQueue<int>(singleReader: true, singleWriter: true);
							using (CancellationTokenSource cts = new CancellationTokenSource(50))
								await Expect.ThrowsAsync<OperationCanceledException>(async () => await q.WaitToReadAsync(cts.Token).ConfigureAwait(false), "cancellation is flow control and must surface as OCE").ConfigureAwait(false);
						}
					),
						("WaitToReadAsync returns (not hangs) on a completed empty queue", async () =>
						{
							ChannelQueue<int> q = new ChannelQueue<int>(singleReader: true, singleWriter: true);
							q.Complete();
							Task wait  = q.WaitToReadAsync(CancellationToken.None).AsTask();
							Task first = await Task.WhenAny(wait, Task.Delay(2000)).ConfigureAwait(false);
							Expect.True(first == wait, "a consumer parked on a dead queue must return, not sleep forever");
						}
					),
						("multi-producer counts stay exact", async () =>
						{
							ChannelQueue<int> q = new ChannelQueue<int>(singleReader: true, singleWriter: false);
							const int kTasks = 8, kPer = 20000;
							Task[] producers = new Task[kTasks];
							for (int t = 0; t < kTasks; t++)
								producers[t] = Task.Run(() => { for (int i = 0; i < kPer; i++) q.Add(1); });
							List<int> drained = new List<int>();
							Task      all     = Task.WhenAll(producers);
							while (all.IsCompleted == false)
								q.MoveTo(drained);
							await all.ConfigureAwait(false);
							q.MoveTo(drained);
							Expect.Eq(kTasks * kPer, drained.Count, "every Add drained exactly once");
							Expect.Eq(            0,       q.Count, "hand-maintained Count lands back at zero");
						}
					));
				}
			}
		}
	}
}