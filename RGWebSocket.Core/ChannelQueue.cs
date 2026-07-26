#nullable enable
#nullable disable warnings
//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// Thin queue wrapper over System.Threading.Channels, shaped like the LockingList usage pattern (Add + MoveTo bulk drain)
// so call sites stay familiar.  Benchmarked (BenchmarkDotNet, .NET 10, i9-14900HX, 2026-07) against the previous
// LockingList+Nito.AsyncAutoResetEvent implementation for this producer/consumer role: 3-5x the throughput and ~1KB vs
// 1-2.8MB allocated per 100k messages (the async event allocated a waiter per sleep/wake cycle; the channel's waiter is
// pooled and its queue segments ring-buffer-reuse when the consumer keeps up).
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

			// Maintained by hand, because ChannelReader.Count is NOT available on the channels we actually use: any
			// SingleReader channel gets the optimized single-consumer implementation, whose CanCount is false and whose
			// Count THROWS NotSupportedException.  Every queue in this library is SingleReader, so reading Reader.Count
			// was an unconditional crash for anyone who enabled RGWS_LOGGING (the Unity client logs it per message).
			private int _count;
			public  int Count => Volatile.Read(ref _count);

			// Returns FALSE if the queue has been Completed -- meaning nobody will ever drain this item, so the caller
			// still owns it and must release whatever it holds.  Ignoring this return value on a queue that carries
			// pooled buffers leaks them: see RGWebSocket.Send.
			public bool Add(T item)
			{
				if (_channel.Writer.TryWrite(item) == false)
					return false;
				Interlocked.Increment(ref _count);
				return true;
			}

			// Permanently close the queue to writers.  Already-queued items remain readable (drain after completing),
			// but every later Add fails instead of silently accepting an item no consumer will ever take.  This is what
			// closes the shutdown race: a producer that checked "still alive" a moment ago finds out its item was not
			// accepted, rather than parking a pooled buffer in a queue whose consumer has already exited.
			public void Complete()
			{
				_channel.Writer.TryComplete();
			}

			// Bulk-drain everything currently queued into the caller's list (appends; caller clears).
			public void MoveTo(List<T> list)
			{
				while (_channel.Reader.TryRead(out T item))
				{
					Interlocked.Decrement(ref _count);
					list.Add(item);
				}
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