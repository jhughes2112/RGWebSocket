//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// PooledArray is the foundation everything else stands on -- a refcount bug here is a use-after-free or a
// cross-connection data leak.  All assertions on the live counters are DELTAS from a captured baseline, so
// these tests don't care what the rest of the run has borrowed (e.g. the close sentinel).

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		namespace UnitTests
		{
			static public class PooledArrayTests
			{
				static public Task Run()
				{
					return Runner.Group("PooledArray",
						("bucket rounding: power-of-two, 128 minimum", () =>
						{
							using (PooledArray pa = PooledArray.BorrowFromPool(1))
							{
								Expect.Eq(  1,      pa.Length, "requested length");
								Expect.Eq(128, pa.data.Length, "1 byte lands in the minimum 128 bucket");
							}
							using (PooledArray pa = PooledArray.BorrowFromPool(0))
							{
								Expect.Eq(  0,      pa.Length, "zero-length borrow is legal (heartbeats)");
								Expect.Eq(128, pa.data.Length, "zero-length still gets the minimum bucket");
							}
							using (PooledArray pa = PooledArray.BorrowFromPool(128))
								Expect.Eq(128, pa.data.Length, "exact bucket size stays put");
							using (PooledArray pa = PooledArray.BorrowFromPool(129))
								Expect.Eq(256, pa.data.Length, "one past a bucket rounds up");
							using (PooledArray pa = PooledArray.BorrowFromPool(4096))
								Expect.Eq(4096, pa.data.Length, "typical receive buffer");
							using (PooledArray pa = PooledArray.BorrowFromPool(4097))
								Expect.Eq(8192, pa.data.Length, "typical receive buffer plus one doubles");
							return Task.CompletedTask;
						}
					),
						("out-of-range requests throw fast (no hang, no allocation)", () =>
						{
							long baseline = PooledArray.GetLiveAllocs();
							Expect.Throws<ArgumentOutOfRangeException>(() => PooledArray.BorrowFromPool(-1), "negative length");
							Expect.Throws<ArgumentOutOfRangeException>(() => PooledArray.BorrowFromPool(PooledArray.kMaxLength + 1), "past kMaxLength (the old pow2 loop would spin forever)");
							Expect.Throws<ArgumentOutOfRangeException>(() => PooledArray.BorrowFromPool(int.MaxValue), "int.MaxValue");
							Expect.Eq(baseline, PooledArray.GetLiveAllocs(), "nothing was allocated by the refusals");
							return Task.CompletedTask;
						}
					),
						("live counters are exact across borrow and return", () =>
						{
							long        baseAllocs = PooledArray.GetLiveAllocs();
							long        baseBytes  = PooledArray.GetLiveAllocSize();
							PooledArray a          = PooledArray.BorrowFromPool(100);  // 128 bucket
							PooledArray b          = PooledArray.BorrowFromPool(5000); // 8192 bucket
							Expect.Eq(baseAllocs + 2, PooledArray.GetLiveAllocs(), "two live buffers");
							Expect.Eq(baseBytes + 128 + 8192, PooledArray.GetLiveAllocSize(), "live bytes are BUCKET sizes, not requested lengths");
							((IDisposable)a).Dispose();
							((IDisposable)b).Dispose();
							Expect.Eq(baseAllocs,    PooledArray.GetLiveAllocs(), "all returned");
							Expect.Eq( baseBytes, PooledArray.GetLiveAllocSize(), "all bytes returned");
							return Task.CompletedTask;
						}
					),
						("recycling reuses the freed buffer (LIFO, cache-warm)", () =>
						{
							PooledArray first     = PooledArray.BorrowFromPool(777);
							byte[]      firstData = first.data;
							((IDisposable)first).Dispose();
							Expect.Eq(-1, first.Length, "a freed buffer's Length is poisoned to -1 so stale readers look wrong immediately");
							PooledArray second = PooledArray.BorrowFromPool(700); // same 1024 bucket
							Expect.True(ReferenceEquals(firstData, second.data), "the freed buffer must be the next one handed out of its bucket");
							Expect.Eq(700, second.Length, "recycled buffer carries the NEW requested length");
							((IDisposable)second).Dispose();
							return Task.CompletedTask;
						}
					),
						("IncRef keeps a buffer alive through one Dispose", () =>
						{
							long        baseline = PooledArray.GetLiveAllocs();
							PooledArray pa       = PooledArray.BorrowFromPool(64);
							pa.IncRef();
							((IDisposable)pa).Dispose();
							Expect.Eq(baseline + 1, PooledArray.GetLiveAllocs(), "still live: the IncRef holder owns it");
							Expect.Eq(          64,                   pa.Length, "length still valid while a reference remains");
							((IDisposable)pa).Dispose();
							Expect.Eq(baseline, PooledArray.GetLiveAllocs(), "released when the last reference drops");
							return Task.CompletedTask;
						}
					),
						("double-Dispose throws AND does not poison the pool for the next borrower", () =>
						{
							PooledArray pa = PooledArray.BorrowFromPool(300); // 512 bucket
							((IDisposable)pa).Dispose();
							Expect.Throws<InvalidOperationException>(() => ((IDisposable)pa).Dispose(), "double-Dispose is a loud caller bug");
							// The offender threw; an INNOCENT later borrower of the same bucket must see a fully healthy buffer.
							PooledArray innocent = PooledArray.BorrowFromPool(300);
							innocent.data[0]     = 0x5A;
							((IDisposable)innocent).Dispose(); // this used to throw, blaming the innocent caller for the earlier double-Dispose
							return Task.CompletedTask;
						}
					),
						("concurrency hammer: counters return to baseline, no refcount faults", async () =>
						{
							long baseAllocs = PooledArray.GetLiveAllocs();
							long baseBytes  = PooledArray.GetLiveAllocSize();
							const int kTasks = 8, kIterations = 20000;
							Task[] tasks = new Task[kTasks];
							for (int t = 0; t < kTasks; t++)
							{
								int seed = 1234 + t;
								tasks[t] = Task.Run(() =>
								{
									Random rng = new Random(seed);
									for (int i = 0; i < kIterations; i++)
									{
										int length = rng.Next(0, 65536);
										using (PooledArray pa = PooledArray.BorrowFromPool(length))
										{
											if (length > 0)
											{
												pa.data[0]          = (byte)i; // touch both ends of the requested range
												pa.data[length - 1] = (byte)i;
												if (pa.data[0] != (byte)i || pa.data[length - 1] != (byte)i)
													throw new Exception("buffer contents changed under us -- two owners of one buffer");
											}
										}
									}
								});
							}
							await Task.WhenAll(tasks).ConfigureAwait(false);
							Expect.Eq(baseAllocs,    PooledArray.GetLiveAllocs(), "live buffer count back to baseline");
							Expect.Eq( baseBytes, PooledArray.GetLiveAllocSize(), "live byte count back to baseline");
						}
					));
				}
			}
		}
	}
}