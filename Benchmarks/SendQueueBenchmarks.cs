//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// The question: is LockingList+AsyncAutoResetEvent (the shipping send-queue pattern) better or worse than
// System.Threading.Channels for RGWebSocket's exact workload?  The workload is: N producers enqueue small structs
// from arbitrary threads, ONE consumer drains everything each wakeup (bulk when possible), in bursts.
//
// Each benchmark op moves 100,000 messages per producer end-to-end and reports total time + total managed allocations.
// What to look at: Allocated column (steady-state garbage per 100k messages) and Mean (throughput).

using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nito.AsyncEx;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		namespace Benchmarks
		{
			// Mirrors RGWebSocket.QueuedSendMsg: a reference, a flag, a timestamp.
			public struct QueuedMsg
			{
				public object payload;
				public bool   isText;
				public long   enqueuedTick;
			}

			[MemoryDiagnoser]
			[ShortRunJob]
			public class SendQueueBenchmarks
			{
				private const int kMessagesPerProducer = 100_000;

				[Params(1, 4)]
				public int Producers;

				// STEADY-STATE DESIGN: the queues and drain lists persist across ops, exactly like a live connection's do.
				// A fresh-per-op design would charge LockingList for one-time backing-array growth that real connections
				// amortize to zero over their lifetime, while Channels' segment churn is a recurring cost either way.
				// Warmup iterations grow everything to high-water; measured iterations then show true per-message cost.
				private LockingList<QueuedMsg> _queue;
				private AsyncAutoResetEvent    _wake;
				private List<QueuedMsg>        _local;
				private Channel<QueuedMsg>     _channel;

				[GlobalSetup]
				public void Setup()
				{
					_queue = new LockingList<QueuedMsg>();
					_wake = new AsyncAutoResetEvent(false);
					_local = new List<QueuedMsg>();
					_channel = Channel.CreateUnbounded<QueuedMsg>(new UnboundedChannelOptions()
					{
						SingleReader = true,                    // matches the one-consumer workload; enables the fast path
						SingleWriter = (Producers == 1),
					});
				}

				[Benchmark(Baseline = true)]
				public async Task LockingList_BulkDrain()
				{
					int total = Producers * kMessagesPerProducer;

					Task consumer = Task.Run(async () =>
					{
						int seen = 0;
						while (seen < total)
						{
							_queue.MoveTo(_local);  // bulk-drain under one lock, exactly like RGWebSocket's send task
							if (_local.Count == 0)
							{
								await _wake.WaitAsync().ConfigureAwait(false);
								continue;
							}
							seen += _local.Count;
							_local.Clear();
						}
					});

					Task[] producers = new Task[Producers];
					for (int p = 0; p < Producers; p++)
					{
						producers[p] = Task.Run(() =>
						{
							for (int i = 0; i < kMessagesPerProducer; i++)
							{
								_queue.Add(new QueuedMsg() { payload = null, isText = false, enqueuedTick = i });
								_wake.Set();
							}
						});
					}
					await Task.WhenAll(producers).ConfigureAwait(false);
					await consumer.ConfigureAwait(false);
				}

				[Benchmark]
				public async Task Channel_Unbounded()
				{
					Channel<QueuedMsg> channel = _channel;
					int total = Producers * kMessagesPerProducer;

					Task consumer = Task.Run(async () =>
					{
						int seen = 0;
						while (seen < total && await channel.Reader.WaitToReadAsync().ConfigureAwait(false))
						{
							while (channel.Reader.TryRead(out QueuedMsg msg))
								seen++;
						}
					});

					Task[] producers = new Task[Producers];
					for (int p = 0; p < Producers; p++)
					{
						producers[p] = Task.Run(() =>
						{
							for (int i = 0; i < kMessagesPerProducer; i++)
								channel.Writer.TryWrite(new QueuedMsg() { payload = null, isText = false, enqueuedTick = i });
						});
					}
					await Task.WhenAll(producers).ConfigureAwait(false);
					await consumer.ConfigureAwait(false);
				}
			}
		}
	}
}
