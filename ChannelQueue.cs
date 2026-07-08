#nullable enable
#nullable disable warnings
//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// Thin queue wrapper over System.Threading.Channels, shaped like the LockingList usage pattern (Add + MoveTo bulk drain)
// so call sites stay familiar.  Benchmarked against LockingList+AsyncAutoResetEvent for the producer/consumer role:
// 3-5x the throughput and ~zero steady-state allocations (see Benchmarks/SendQueueBenchmarks.cs for the receipt).
// NOTE this is strictly FIFO -- it deliberately does NOT pretend to be a list (no Remove/PopBack/foreach).  LockingList
// remains the right tool where those matter, e.g. the PooledArray free-lists that want LIFO for cache-warm reuse.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		public class ChannelQueue<T>
		{
			private readonly Channel<T> _channel;

			// Be truthful about reader/writer counts -- the channel picks faster internal paths when it can trust them.
			public ChannelQueue(bool singleReader, bool singleWriter)
			{
				_channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions() { SingleReader = singleReader, SingleWriter = singleWriter });
			}

			public int Count => _channel.Reader.Count;

			public void Add(T item)
			{
				_channel.Writer.TryWrite(item);  // unbounded channel: TryWrite cannot fail while the channel is open, and we never complete it
			}

			// Bulk-drain everything currently queued into the caller's list (appends; caller clears).
			public void MoveTo(List<T> list)
			{
				while (_channel.Reader.TryRead(out T item))
					list.Add(item);
			}

			// Park until something is available to read or the token cancels (throws OperationCanceledException, flow control).
			// Amortized allocation-free: the channel reuses a pooled waiter internally.
			public async ValueTask WaitToReadAsync(CancellationToken token)
			{
				await _channel.Reader.WaitToReadAsync(token).ConfigureAwait(false);
			}
		}
	}
}
