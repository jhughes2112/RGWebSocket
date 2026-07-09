#nullable enable
//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// Central knobs for the websocket library.  Everything a developer might reasonably tune lives here, so nothing requires
// a code change.  These are process-wide statics: call Configure() ONCE at startup (it validates and throws on nonsense),
// or don't call it at all and accept the defaults.  Values are getter-only so nothing can be mutated piecemeal at runtime
// -- the socket pumps read these constantly and are not synchronized against changes.  To make that unbreakable rather
// than merely documented, Configure() throws if any server or socket has already been created.

using System;
using System.Threading;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		static public class RGWebSocketConfig
		{
			// Buffer size for receiving chunks of messages off the wire.
			static public int ReceiveBufferBytes { get; private set; } = 4096;

			// Inbound circuit breaker: a connection that sends a single message bigger than this is disconnected mid-accumulation.
			// Without this, a hostile client can OOM the server with one endless fragmented message.
			static public int MaxInboundMessageBytes { get; private set; } = 16*1024*1024;

			// Outbound circuit breaker: a connection whose unsent queue exceeds this is too slow to keep up (dead client, saturated
			// pipe), so it is disconnected rather than hoarding memory on its behalf.  Deciding which messages would have been
			// discardable is an application-level concern that does not belong at this layer.
			static public int MaxUnsentBytes { get; private set; } = 4*1024*1024;

			// Server-side idle sweep: sockets that have not RECEIVED any data for this many seconds are disconnected.  0 disables it.
			// This measures received data only -- successful sends prove nothing about liveness, they just fill kernel buffers.  If you
			// enable this, clients must send something (a heartbeat) more often than this interval.  This exists because transport-level
			// idle timeouts (HttpListener.IdleConnection, load balancer timeouts) are unreliable behind L7 proxies/Ingress, which keep
			// their own upstream connections warm; an application-level sweep is the only idle timeout you actually own.
			//
			// TRAP: websocket protocol ping/pong frames (e.g. ClientWebSocket.KeepAliveInterval) do NOT count as liveness -- .NET
			// handles them internally and they never surface as received data, so they never refresh the idle timer.  Your heartbeat
			// must be an actual message.  A zero-length message is perfectly legal, costs a few bytes on the wire, is dispatched to
			// your receive callback like any other message, and refreshes the idle timer -- it makes a fine heartbeat if your
			// protocol doesn't already have one.
			static public int IdleDisconnectSeconds { get; private set; } = 0;

			// How often the idle sweep wakes up to check, when enabled.
			static public int IdleSweepPeriodSeconds { get; private set; } = 60;

			// Once anything reads the config to build a socket or server, reconfiguration would race the pumps -- so it's forbidden.
			static private int _inUse = 0;
			static internal void MarkInUse() { Interlocked.Exchange(ref _inUse, 1); }

			// Call once at startup, before creating any servers or sockets.  Validates everything and throws a descriptive
			// exception on nonsense, so a bad config dies loudly at the line that caused it instead of as a mysterious
			// busy-loop or instant-disconnect later.  Skip the call entirely to accept the defaults.
			static public void Configure(int receiveBufferBytes, int maxInboundMessageBytes, int maxUnsentBytes, int idleDisconnectSeconds, int idleSweepPeriodSeconds)
			{
				if (Interlocked.CompareExchange(ref _inUse, 0, 0)!=0)
					throw new InvalidOperationException("RGWebSocketConfig.Configure must be called before any servers or sockets are created -- the pumps read these values unsynchronized.");
				if (receiveBufferBytes<128)
					throw new ArgumentOutOfRangeException(nameof(receiveBufferBytes), receiveBufferBytes, "Must be at least 128 (the minimum pool bucket size).");
				if (maxInboundMessageBytes<receiveBufferBytes)
					throw new ArgumentOutOfRangeException(nameof(maxInboundMessageBytes), maxInboundMessageBytes, $"Must be at least receiveBufferBytes ({receiveBufferBytes}), or no message could ever be received.");
				if (maxUnsentBytes<=0)
					throw new ArgumentOutOfRangeException(nameof(maxUnsentBytes), maxUnsentBytes, "Must be positive, or every connection would be disconnected on its first send.");
				if (idleDisconnectSeconds<0)
					throw new ArgumentOutOfRangeException(nameof(idleDisconnectSeconds), idleDisconnectSeconds, "Negative makes no sense; use 0 to disable the idle sweep.");
				if (idleDisconnectSeconds>0 && idleSweepPeriodSeconds<=0)
					throw new ArgumentOutOfRangeException(nameof(idleSweepPeriodSeconds), idleSweepPeriodSeconds, "Must be positive when the idle sweep is enabled, or the sweep becomes a busy-loop.");

				ReceiveBufferBytes     = receiveBufferBytes;
				MaxInboundMessageBytes = maxInboundMessageBytes;
				MaxUnsentBytes         = maxUnsentBytes;
				IdleDisconnectSeconds  = idleDisconnectSeconds;
				IdleSweepPeriodSeconds = idleSweepPeriodSeconds;
			}
		}
	}
}
