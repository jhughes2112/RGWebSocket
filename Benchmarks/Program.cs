//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// Benchmark entry point.  Run with:
//   dotnet run --project Benchmarks -c Release -- --filter *SendQueue*
//   dotnet run --project Benchmarks -c Release -- --filter *PooledArray*
// Results land in BenchmarkDotNet.Artifacts/ (gitignored).

using BenchmarkDotNet.Running;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		namespace Benchmarks
		{
			public static class Program
			{
				public static void Main(string[] args)
				{
					BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
				}
			}
		}
	}
}
