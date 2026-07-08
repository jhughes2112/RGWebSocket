//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// PooledArray borrow/return cost versus raw GC allocation, single-threaded and with 4 threads hammering the
// same size bucket (worst-case contention on one LockingList).  Each op does 1,000 borrow/dispose cycles.
// What to look at: Allocated should be ~0 for the pool (that's its whole job) and Mean shows the locking overhead
// you pay for it.  The GC baseline allocates Size bytes per cycle and defers its real cost to collections,
// which BenchmarkDotNet surfaces in the Gen0 column.

using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		namespace Benchmarks
		{
			[MemoryDiagnoser]
			[ShortRunJob]
			public class PooledArrayBenchmarks
			{
				private const int kCyclesPerOp = 1_000;

				[Params(256, 65536)]
				public int Size;

				[Benchmark(Baseline = true)]
				public void Pool_BorrowDispose()
				{
					for (int i = 0; i < kCyclesPerOp; i++)
					{
						PooledArray pa = PooledArray.BorrowFromPool(Size);
						pa.data[0] = 1;
						using (pa) { }
					}
				}

				[Benchmark]
				public void GC_NewByteArray()
				{
					for (int i = 0; i < kCyclesPerOp; i++)
					{
						byte[] b = new byte[Size];
						b[0] = 1;
					}
				}

				[Benchmark]
				public void Pool_BorrowDispose_4Threads()
				{
					Task[] tasks = new Task[4];
					for (int t = 0; t < 4; t++)
					{
						tasks[t] = Task.Run(() =>
						{
							for (int i = 0; i < kCyclesPerOp/4; i++)
							{
								PooledArray pa = PooledArray.BorrowFromPool(Size);
								pa.data[0] = 1;
								using (pa) { }
							}
						});
					}
					Task.WaitAll(tasks);
				}
			}
		}
	}
}
