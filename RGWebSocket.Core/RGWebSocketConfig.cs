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
			static public int MaxInboundMessageBytes { get; private set; } = 16 * 1024 * 1024;

			// Outbound circuit breaker: a connection whose unsent queue exceeds this is too slow to keep up (dead client, saturated
			// pipe), so it is disconnected rather than hoarding memory on its behalf.  Deciding which messages would have been
			// discardable is an application-level concern that does not belong at this layer.
			static public int MaxUnsentBytes { get; private set; } = 4 * 1024 * 1024;

			// Client-side inbound backlog breaker (RGUnityWebSocket): received messages wait in a queue until the game loop
			// calls ReceiveAll.  If the loop stalls or the server simply outruns it, that queue grows without limit and every
			// message in it pins a pooled buffer -- an OOM on a memory-tight device.  This is the mirror image of
			// MaxUnsentBytes.  0 disables it.  Exceeding it disconnects with EDisconnectReason.InboundBacklog rather than
			// dropping messages, because silently losing messages corrupts a stateful protocol in ways that are far harder
			// to debug than a disconnect.
			static public int MaxRecvQueueBytes { get; private set; } = 8 * 1024 * 1024;

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

			// Websocket PROTOCOL keepalive (ping/pong) interval, both directions.  0 disables it.
			//
			// Be clear about what this does and does not buy you, because it is easy to over-trust.  In a real deployment
			// the connection is almost always client <-> ingress/proxy <-> server, and the last inch is frequently plain
			// ws:// between the load balancer and this process.  The keepalive you configure here therefore only covers
			// the segment it is actually on -- server <-> load balancer -- and says NOTHING about whether the client is
			// still alive; the proxy keeps its upstream connection warm regardless.  On top of that, .NET answers pings
			// internally, so an inbound ping never surfaces as received data and never refreshes IdleDisconnectSeconds.
			// The idle sweep is the liveness mechanism; this is just enough periodic traffic to stop an intermediary from
			// idling the connection out.  That is a job for tens of seconds, not one second: at one ping per second a
			// server holding 40,000 sockets spends 40,000 packets/sec proving nothing, and the cost scales with exactly
			// the connection count an attacker gets to choose.  Keep it well under your proxy's idle timeout (commonly
			// 60s) and nowhere near it in frequency.
			static public int WebSocketKeepAliveSeconds { get; private set; } = 30;

			// The above as .NET wants it: Timeout.InfiniteTimeSpan is how both ClientWebSocket and HttpListener spell "off".
			static public TimeSpan WebSocketKeepAliveInterval => WebSocketKeepAliveSeconds <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(WebSocketKeepAliveSeconds);

			// Connection-count circuit breaker: when this many websockets are live, further upgrade requests are refused
			// with a 503 (visible in Metrics as refused upgrades).  0 disables it.  Every other breaker in this class is
			// per-connection; without this one, an attacker simply opens connections until the process dies of memory or
			// handle exhaustion.  Size it from your capacity math (each connection pins at least ReceiveBufferBytes plus
			// OS socket state), leaving headroom, because the cap is approximate: upgrades mid-handshake aren't counted
			// yet, so a burst can briefly overshoot by the number of in-flight handshakes.
			static public int MaxConcurrentWebSockets { get; private set; } = 0;

			// Once anything reads the config to build a socket or server, reconfiguration would race the pumps -- so it's forbidden.
			static private  int  _inUse = 0;
			static internal void MarkInUse() { Interlocked.Exchange(ref _inUse, 1); }

			// Call once at startup, before creating any servers or sockets.  Validates everything and throws a descriptive
			// exception on nonsense, so a bad config dies loudly at the line that caused it instead of as a mysterious
			// busy-loop or instant-disconnect later.  Skip the call entirely to accept the defaults.
			static public void Configure(int receiveBufferBytes, int maxInboundMessageBytes, int maxUnsentBytes, int maxRecvQueueBytes, int idleDisconnectSeconds, int idleSweepPeriodSeconds, int webSocketKeepAliveSeconds, int maxConcurrentWebSockets)
			{
				if (webSocketKeepAliveSeconds < 0)
					throw new ArgumentOutOfRangeException(nameof(webSocketKeepAliveSeconds), webSocketKeepAliveSeconds, "Negative makes no sense; use 0 to disable protocol keepalives.");
				if (webSocketKeepAliveSeconds > 0 && webSocketKeepAliveSeconds < 5)
					throw new ArgumentOutOfRangeException(nameof(webSocketKeepAliveSeconds), webSocketKeepAliveSeconds, "Anything under 5s is a per-socket packet storm that buys nothing -- this only keeps an intermediary from idling the connection out, so tens of seconds is the right scale.  Use 0 to disable.");
				if (maxRecvQueueBytes < 0)
					throw new ArgumentOutOfRangeException(nameof(maxRecvQueueBytes), maxRecvQueueBytes, "Negative makes no sense; use 0 to disable the inbound backlog breaker.");
				if (Interlocked.CompareExchange(ref _inUse, 0, 0) != 0)
					throw new InvalidOperationException("RGWebSocketConfig.Configure must be called before any servers or sockets are created -- the pumps read these values unsynchronized.");
				if (maxConcurrentWebSockets < 0)
					throw new ArgumentOutOfRangeException(nameof(maxConcurrentWebSockets), maxConcurrentWebSockets, "Negative makes no sense; use 0 to disable the connection-count cap.");
				if (receiveBufferBytes < 128 || receiveBufferBytes > PooledArray.kMaxLength)
					throw new ArgumentOutOfRangeException(nameof(receiveBufferBytes), receiveBufferBytes, $"Must be 128 (the minimum pool bucket size) to {PooledArray.kMaxLength} (the largest pooled buffer).");
				if (maxInboundMessageBytes < receiveBufferBytes || maxInboundMessageBytes > PooledArray.kMaxLength)
					throw new ArgumentOutOfRangeException(nameof(maxInboundMessageBytes), maxInboundMessageBytes, $"Must be at least receiveBufferBytes ({receiveBufferBytes}) or no message could ever be received, and at most {PooledArray.kMaxLength} -- the receive path grows buffers by doubling, and past 1GB that math overflows int.");
				if (maxUnsentBytes <= 0)
					throw new ArgumentOutOfRangeException(nameof(maxUnsentBytes), maxUnsentBytes, "Must be positive, or every connection would be disconnected on its first send.");
				if (idleDisconnectSeconds < 0)
					throw new ArgumentOutOfRangeException(nameof(idleDisconnectSeconds), idleDisconnectSeconds, "Negative makes no sense; use 0 to disable the idle sweep.");
				if (idleDisconnectSeconds > 0 && idleSweepPeriodSeconds <= 0)
					throw new ArgumentOutOfRangeException(nameof(idleSweepPeriodSeconds), idleSweepPeriodSeconds, "Must be positive when the idle sweep is enabled, or the sweep becomes a busy-loop.");

				ReceiveBufferBytes        = receiveBufferBytes;
				MaxInboundMessageBytes    = maxInboundMessageBytes;
				MaxUnsentBytes            = maxUnsentBytes;
				MaxRecvQueueBytes         = maxRecvQueueBytes;
				IdleDisconnectSeconds     = idleDisconnectSeconds;
				IdleSweepPeriodSeconds    = idleSweepPeriodSeconds;
				WebSocketKeepAliveSeconds = webSocketKeepAliveSeconds;
				MaxConcurrentWebSockets   = maxConcurrentWebSockets;
			}
		}
	}
}