#nullable enable
//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// Central knobs for the websocket library.  Everything a developer might reasonably tune lives here, so nothing requires a code change.
// Pass one instance to WebServer/WebSocketServer/UnityWebSocket at startup (they hand it to every RGWebSocket they create); pass
// RGWebSocketConfig.Default if the defaults suit you -- the parameter is deliberately required so the choice is always explicit.
// Set values once at startup -- these are read constantly by the socket pumps and are not synchronized.
// (int reads are atomic so nothing breaks if you mutate mid-run, but per-socket behavior may be briefly inconsistent.)

namespace ReachableGames
{
	namespace RGWebSocket
	{
		public class RGWebSocketConfig
		{
			static public readonly RGWebSocketConfig Default = new RGWebSocketConfig();

			// Buffer size for receiving chunks of messages off the wire.
			public int ReceiveBufferBytes { get; set; } = 4096;

			// Inbound circuit breaker: a connection that sends a single message bigger than this is disconnected mid-accumulation.
			// Without this, a hostile client can OOM the server with one endless fragmented message.
			public int MaxInboundMessageBytes { get; set; } = 16*1024*1024;

			// Outbound circuit breaker: a connection whose unsent queue exceeds this is too slow to keep up (dead client, saturated
			// pipe), so it is disconnected rather than hoarding memory on its behalf.  Deciding which messages would have been
			// discardable is an application-level concern that does not belong at this layer.
			public int MaxUnsentBytes { get; set; } = 4*1024*1024;

			// Server-side idle sweep: sockets that have not RECEIVED any data for this many seconds are disconnected.  0 disables it.
			// This measures received data only -- successful sends prove nothing about liveness, they just fill kernel buffers.  If you
			// enable this, clients must send something (a heartbeat) more often than this interval.  This exists because transport-level
			// idle timeouts (HttpListener.IdleConnection, load balancer timeouts) are unreliable behind L7 proxies/Ingress, which keep
			// their own upstream connections warm; an application-level sweep is the only idle timeout you actually own.
			public int IdleDisconnectSeconds { get; set; } = 0;

			// How often the idle sweep wakes up to check, when enabled.
			public int IdleSweepPeriodSeconds { get; set; } = 60;
		}
	}
}
