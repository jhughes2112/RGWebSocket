#nullable enable
//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// THE server-side base class: derive from this to manage a bunch of websocket connections.  There is deliberately no
// public interface underneath -- this is the one entry point, and the default path is typed messages: your OnMessage
// receives strongly typed objects, no PooledArray ever crosses into your code, and there is no refcount discipline to
// get wrong.  Deserialization happens on each socket's receive task, so parsing parallelizes across connections.
//
// STRICT BY DESIGN: the typed pipeline is binary-only, and a peer that sends a text frame, a runt frame (under 4 bytes),
// an unknown type id, or a payload the factory rejects is disconnected with EDisconnectReason.ProtocolError.  A peer
// speaking the wrong protocol is broken or hostile; lenience is how protocol confusion festers.  The disconnect shows
// up by cause in WebSocketServerMetrics, which is exactly where you want to see a spike during an attack.
//
// RAW ESCAPE HATCH: if you need text frames or your own framing (the ChatTest chat server is a working example), use
// the logger-only constructor and override OnRawMessage -- you get every message as (PooledArray, isText) and you own
// the protocol.  The buffer is released by the library when your override returns; IncRef it if you keep it.

using System;
using System.Buffers.Binary;
using System.Net;
using System.Threading.Tasks;
using Logging;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		public abstract class RGConnectionManager
		{
			private readonly IMessageFactory? _factory;  // null = raw mode; the deriver overrides OnRawMessage instead
			protected readonly ILogging _logger;

			// Typed mode (the normal case): messages flow through the factory's codec.
			protected RGConnectionManager(IMessageFactory factory, ILogging logger)
			{
				if (factory==null)
					throw new ArgumentNullException(nameof(factory));
				if (logger==null)
					throw new ArgumentNullException(nameof(logger));
				_factory = factory;
				_logger = logger;
			}

			// Raw mode (advanced): no factory; you MUST override OnRawMessage and speak your own protocol.
			protected RGConnectionManager(ILogging logger)
			{
				if (logger==null)
					throw new ArgumentNullException(nameof(logger));
				_factory = null;
				_logger = logger;
			}

			// Your side of the contract.  OnConnection runs before the socket's pumps start (nothing can arrive until you
			// return).  OnDisconnect runs on the dying socket's send task -- set flags, don't await Shutdown there.
			// OnMessage runs on the socket's receive task: one message at a time per socket, but different sockets call
			// concurrently, and blocking here stalls that socket's receive pump.  Shutdown must Close() every socket you track.
			public abstract Task OnConnection(RGWebSocket rgws, HttpListenerContext context);
			public abstract Task OnDisconnect(RGWebSocket rgws);
			public abstract Task OnMessage(RGWebSocket rgws, IRGMessage msg);  // never called in raw mode; stub it with Task.CompletedTask
			public abstract Task Shutdown();

			// Serialize and queue a typed message.  Safe from any thread.
			public void Send(RGWebSocket rgws, IRGMessage msg)
			{
				if (_factory==null)
					throw new InvalidOperationException("This RGConnectionManager was constructed in raw mode (no IMessageFactory); send with rgws.Send() directly.");
				using (PooledArray buffer = RGMessagePacker.Pack(_factory, msg))  // the send queue takes its own reference; ours releases here
				{
					rgws.Send(buffer);
				}
			}

			// Every inbound message lands here, on the socket's receive task.  The default implementation IS the typed
			// pipeline.  Override it (with the raw-mode constructor) to speak your own protocol; do not call base if you do.
			protected internal virtual Task OnRawMessage(RGWebSocket rgws, PooledArray msg, bool isText)
			{
				if (_factory==null)  // raw-mode construction without overriding OnRawMessage is a config error, and every message would hit it -- kill the connection so it's loud
				{
					_logger.Log(EVerbosity.Error, $"RGConnectionManager: raw mode (no IMessageFactory) but OnRawMessage is not overridden.  Disconnecting {rgws.DisplayId} (ProtocolError).");
					rgws.Close(EDisconnectReason.ProtocolError);
					return Task.CompletedTask;
				}
				if (isText)
				{
					_logger.Log(EVerbosity.Warning, $"RGConnectionManager: text frame from {rgws.DisplayId} on a binary-only typed connection.  Disconnecting (ProtocolError).");
					rgws.Close(EDisconnectReason.ProtocolError);
					return Task.CompletedTask;
				}
				if (msg.Length<4)  // runt frame: not even a type id header
				{
					_logger.Log(EVerbosity.Warning, $"RGConnectionManager: runt frame ({msg.Length} bytes) from {rgws.DisplayId}.  Disconnecting (ProtocolError).");
					rgws.Close(EDisconnectReason.ProtocolError);
					return Task.CompletedTask;
				}
				int typeId = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(msg.data, 0, 4));
				IRGMessage? typed = _factory.Deserialize(typeId, new ReadOnlySpan<byte>(msg.data, 4, msg.Length-4));  // span: the compiler guarantees the pooled buffer cannot escape this call
				if (typed==null)
				{
					_logger.Log(EVerbosity.Warning, $"RGConnectionManager: factory rejected message typeId={typeId} len={msg.Length-4} from {rgws.DisplayId}.  Disconnecting (ProtocolError).");
					rgws.Close(EDisconnectReason.ProtocolError);
					return Task.CompletedTask;
				}
				return OnMessage(rgws, typed);
			}
		}
	}
}
