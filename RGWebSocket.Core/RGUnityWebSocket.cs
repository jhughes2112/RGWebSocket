#nullable enable
//-------------------
// Reachable Games
// Copyright 2023
//-------------------
// Uncomment to provide more detailed logging.  Errors from exceptions are always logged, as is the final stats for a closed socket.
//#define RGWS_LOGGING

using Logging;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		// This enforces the everything-happens-on-main-thread requirement to work with Unity as a game platform.  This has been built in such a way to allow
		// you to connect/close/connect multiple times without having to destroy it.  In many cases, you will want to reconnect on foregrounding and disconnection
		// (probably) happens automatically on backgrounding, so important to make it easy.
		public class RGUnityWebSocket
		{
			// This tracks the status of RGWS, which is hard to determine directly from inspection due to async operations.
			private enum Status
			{
				ReadyToConnect,
				Connecting,
				Connected,
			}

			// Supports strings or binary messages on the websocket.  Both kinds live in the same pooled buffer; isText tells you which.
			// ALL messages must be disposed by the consumer to return the buffer to the pool.  Use .Text to decode a text payload when you want the string.
			public struct wsMessage
			{
				public PooledArray msg;    // the payload bytes; dispose when done
				public bool        isText; // true = UTF8 text payload
				public string      Text => System.Text.Encoding.UTF8.GetString(msg.data, 0, msg.Length); // decode helper, only pay for the string when you ask for it
			}
			private ChannelQueue<wsMessage> _incomingMessages = new ChannelQueue<wsMessage>(singleReader: true, singleWriter: true); // written only by the recv task, drained only by the main thread's ReceiveAll poll
			private int                     _queuedRecvBytes  = 0;                     // RETAINED bytes sitting in _incomingMessages (see QueueCharge), so the backlog breaker sees the real memory cost
			private int                     _backlogTripped   = 0;                     // latch: the backlog breaker fires (and logs) exactly once per connection
			private List<wsMessage>         _drainScratch     = new List<wsMessage>(); // used to empty the queue on teardown

			// What one queued message actually costs, same reasoning as RGWebSocket.QueueCharge: a message occupies its
			// whole power-of-two pool bucket (a 1-byte frame received into a 4KB buffer retains 4KB) plus a queue slot,
			// so charging payload Length would let zero-length and tiny frames pile up unbounded under a budget that
			// still reads near zero.  Charge the capacity we actually hold.
			private const  int kQueuedMessageOverheadBytes = 256;
			static private int QueueCharge(PooledArray msg) { return msg.data.Length + kQueuedMessageOverheadBytes; }

			private          string                     _connectUrl; // caching the connection params so Reconnect is possible w/o downstream users needing to know the details
			private          int                        _connectTimeoutMS;
			private          Dictionary<string, string> _connectHeaders = new Dictionary<string, string>();
			private volatile Status                     _status         = Status.ReadyToConnect; // written on the socket's send thread (OnDisconnect), read on the main thread

			private RGWebSocket? _rgws; // This should only be non-null when _status==Connected.
			private ILogging     _logger;
			private string       _loggerPrefix = "";
			private string       _lastErrorMsg = string.Empty;
			// WARNING: this fires on the dying socket's SEND TASK.  Do not call Shutdown(), Reconnect(), or Connect() from
			// it -- all three wait for that send task to finish, which is the task running your callback, so it waits for
			// itself and hangs forever.  Set a flag and act on it from your own update loop.
			private          Action<RGUnityWebSocket>? _disconnectCallback;
			private readonly IMessageFactory?          _factory; // null = raw mode (wsMessage API); non-null enables the typed Send/ReceiveAll
			private          List<wsMessage>           _typedScratch = new List<wsMessage>(); // scratch for the typed ReceiveAll; single consumer (main thread)

			//-------------------
			// Trivial accessors
			public bool              IsConnected    => _status == Status.Connected;
			public bool              IsConnecting   => _status == Status.Connecting;
			public bool              IsDisconnected => _status == Status.ReadyToConnect;
			public string            LastError { get { return _rgws?.LastError ?? _lastErrorMsg; } private set { _lastErrorMsg = value; } }
			public EDisconnectReason DisconnectReason => _rgws?.DisconnectReason ?? EDisconnectReason.None; // why the last connection died; None once Shutdown has cleared it
			public void              GetStats(out int sentMsgs, out long sentBytes, out int recvMsgs, out long recvBytes)
			{
				if (_rgws != null)
				{
					sentMsgs  = _rgws.SentMessages;
					sentBytes = _rgws.SentBytes;
					recvMsgs  = _rgws.RecvMessages;
					recvBytes = _rgws.RecvBytes;
				}
				else
				{
					sentMsgs  = 0;
					sentBytes = 0;
					recvMsgs  = 0;
					recvBytes = 0;
				}
			}

			//-------------------

			// Raw mode: you speak your own protocol via Send(string)/Send(PooledArray) and ReceiveAll(List<wsMessage>).
			// disconnectCallback may be null if you poll IsDisconnected instead.
			public RGUnityWebSocket(ILogging logger, string loggerPrefix, Action<RGUnityWebSocket>? disconnectCallback, int connectTimeoutMS)
			{
				RGWebSocketConfig.MarkInUse(); // from here on, Configure() throws -- the pumps read the config unsynchronized
				_logger             = logger;
				_loggerPrefix       = loggerPrefix;
				_disconnectCallback = disconnectCallback;
				_connectTimeoutMS   = connectTimeoutMS;
				_connectUrl         = string.Empty;
				_factory            = null;
			}

			// Typed mode (the normal case): Send(IRGMessage) and ReceiveAll(List<IRGMessage>) run everything through the
			// factory's codec, and no pooled buffer ever reaches your code.
			public RGUnityWebSocket(ILogging logger, string loggerPrefix, Action<RGUnityWebSocket>? disconnectCallback, int connectTimeoutMS, IMessageFactory factory)
				: this(logger, loggerPrefix, disconnectCallback, connectTimeoutMS)
			{
				if (factory == null)
					throw new ArgumentNullException(nameof(factory));
				_factory = factory;
			}

			// Lets you specify where to connect to.
			public Task Connect(string url, Dictionary<string, string> headers)
			{
				_connectUrl     = url;
				_connectHeaders = headers;

				return DoConnection();
			}

			// This uses whatever the connect url and timeout were previously set to.
			public Task Reconnect()
			{
				return DoConnection();
			}

			// Does all the real work of making a connection.  Currently, this blocks on the initial connection.
			// I'd rather there be a cleaner interface for this, where the Task itself is being polled and the state changes over when it's done.
			private async Task DoConnection()
			{
				if (_status != Status.ReadyToConnect)
					throw new InvalidOperationException("RGUnityWebSocket cannot Connect/Reconnect unless status is ReadyToConnect.  Call Shutdown() first.");
				if (_rgws != null)
					Shutdown(); // the previous connection ended remotely and was never explicitly Shutdown; dispose it so Connect/Reconnect doesn't leak it

				_lastErrorMsg = string.Empty;
				Uri uri       = new Uri(_connectUrl); // I think this can throw exceptions for bad formatting?

				// Creates a websocket connection and lets you start sending or receiving messages on separate threads.
				ClientWebSocket wsClient = new ClientWebSocket();
				try
				{
					// Protocol keepalive, from the same knob the server uses (0 disables).  On this side it covers the
					// client<->proxy hop, which is the segment that keeps NAT mappings and the proxy's downstream
					// connection from idling out -- worth having on mobile.  It is NOT a substitute for your app-level
					// heartbeat: .NET answers pings internally, so they never refresh the server's idle sweep.
					wsClient.Options.KeepAliveInterval = RGWebSocketConfig.WebSocketKeepAliveInterval;
					using (CancellationTokenSource connectTimeout = new CancellationTokenSource(_connectTimeoutMS))
					{
						// Apply all the headers that were passed in.
						foreach (KeyValuePair<string, string> kvp in _connectHeaders)
						{
							wsClient.Options.SetRequestHeader(kvp.Key, kvp.Value);
						}

						_status = Status.Connecting;
						await wsClient.ConnectAsync(uri, connectTimeout.Token).ConfigureAwait(false);
						_logger.Log(EVerbosity.Debug, $"{_loggerPrefix} UWS Connected to {uri} http part");
					}

					Interlocked.Exchange(ref _backlogTripped, 0); // fresh connection, fresh latch
					_rgws   = new RGWebSocket(null, OnReceive, OnDisconnect, _logger, uri.ToString(), wsClient);
					_status = Status.Connected;
					_rgws.Start(); // pumps spin up only after everything is fully wired
					_logger.Log(EVerbosity.Debug, $"{_loggerPrefix} UWS Connected to {uri} rgws part");
				}
				catch (OperationCanceledException) // await unwraps exceptions, so the timeout arrives directly (no AggregateException)
				{
					_lastErrorMsg = "Connection timed out.";
					_logger.Log(EVerbosity.Error, $"{_loggerPrefix} {_lastErrorMsg}");
					wsClient.Dispose(); // cleanup
					Shutdown();         // this just resets everything so we can try connecting again
				}
				catch (Exception e)
				{
					_lastErrorMsg = e.Message; // WebSocketException and friends all carry a useful Message
					_logger.Log(EVerbosity.Error, $"{_loggerPrefix} {_lastErrorMsg}");
					wsClient.Dispose(); // cleanup
					Shutdown();         // this just resets everything so we can try connecting again
				}
			}

			// This is a friendly close, where we tell the other side and they shake on it.
			public void Close()
			{
				RGWebSocket? rgws = _rgws; // snapshot: Shutdown() on another thread nulls the field before _status changes
				if (_status == Status.Connected && rgws != null)
				{
					rgws.Close();
					_logger.Log(EVerbosity.Debug, $"{_loggerPrefix} UWS Closed.");
				}
			}

			// Tears everything down and resets so Connect/Reconnect is legal again.  This is the one to await from
			// per-frame/game-loop code: the close handshake can take real time on a bad network, and awaiting it costs nothing.
			public async Task ShutdownAsync()
			{
				if (_rgws != null)
				{
					RGWebSocket rgws = _rgws; // prevent recursion here in shutdown
					_rgws            = null;
					await rgws.Shutdown().ConfigureAwait(false);
					_logger.Log(EVerbosity.Debug, $"{_loggerPrefix} UWS connection reset.");
				}
				DrainIncoming(); // AFTER the pumps are stopped, so nothing can be enqueued behind us
				_status = Status.ReadyToConnect;
			}

			// Empty the inbound queue and release every buffer in it.  Undelivered messages MUST NOT survive teardown:
			// each one holds an IncRef'd pooled buffer (so they would leak for the life of the process), and worse, they
			// would be handed to the NEXT session by ReceiveAll -- stale messages from a dead connection arriving as if
			// they were current.  Called on shutdown, which is the only moment both pumps are guaranteed stopped.
			private void DrainIncoming()
			{
				_incomingMessages.MoveTo(_drainScratch);
				int droppedBytes = 0;
				for (int i = 0; i < _drainScratch.Count; i++)
				{
					droppedBytes += _drainScratch[i].msg.Length;
					using (_drainScratch[i].msg)
					{ }
				}
				if (_drainScratch.Count > 0)
					_logger.Log(EVerbosity.Debug, $"{_loggerPrefix} UWS discarded {_drainScratch.Count} undelivered inbound messages ({droppedBytes} bytes) on teardown.");
				_drainScratch.Clear();
				Interlocked.Exchange(ref _queuedRecvBytes, 0);
			}

			// Blocking convenience wrapper, fine for application teardown.  DO NOT call this from a game loop or UI thread --
			// it synchronously waits out the close handshake, which is a frame hitch waiting for a bad network day.
			// Use ShutdownAsync there instead.
			public void Shutdown()
			{
				ShutdownAsync().GetAwaiter().GetResult();
			}

			// Returns false if data could not be sent (eg. you aren't connected or in a good status to do so)
			public bool Send(string msg)
			{
				RGWebSocket? rgws = _rgws; // snapshot: Shutdown() on another thread nulls the field before _status changes
				if (_status == Status.Connected && rgws != null)
				{
					rgws.Send(msg);
#if RGWS_LOGGING
					_logger.Log(EVerbosity.Debug, $"{_loggerPrefix} UWS Sent {msg.Length} bytes");
#endif
					return true;
				}
				else
				{
					_logger.Log(EVerbosity.Error, $"{_loggerPrefix} UWS Send called but status is {_status}");
				}
				return false;
			}

			// Returns false if data could not be sent (eg. you aren't connected or in a good status to do so)
			public bool Send(PooledArray msg)
			{
				RGWebSocket? rgws = _rgws; // snapshot: Shutdown() on another thread nulls the field before _status changes
				if (_status == Status.Connected && rgws != null)
				{
					rgws.Send(msg);
#if RGWS_LOGGING
					_logger.Log(EVerbosity.Debug, $"{_loggerPrefix} UWS Sent {msg.Length} bytes");
#endif
					return true;
				}
				else
				{
					_logger.Log(EVerbosity.Error, $"{_loggerPrefix} UWS Send called but status is {_status}");
				}
				return false;
			}

			// This is intended for you to grab all the messages that have been sent, in bulk and from the main thread, like in an MonoBehaviour.Update() method.
			// NOTE: EVERY message (text and binary alike) must be disposed to return its buffer to the pool.  You own these messages now!
			public void ReceiveAll(List<wsMessage> messageList)
			{
				// Take the whole set of incoming messages, lock it, then move it to messageList and clear it out
				int before = messageList.Count;
				_incomingMessages.MoveTo(messageList);
				int drained = 0;
				for (int i = before; i < messageList.Count; i++)
					drained += QueueCharge(messageList[i].msg); // credit back exactly what OnReceive charged
				if (drained > 0)
					Interlocked.Add(ref _queuedRecvBytes, -drained); // these buffers are the caller's problem now, not the backlog's
			}

			//-------------------
			// Typed API -- requires the IMessageFactory constructor.

			// Serialize and queue a typed message.  Returns false if not connected.
			public bool Send(IRGMessage msg)
			{
				if (_factory == null)
					throw new InvalidOperationException("This RGUnityWebSocket was constructed in raw mode; use the IMessageFactory constructor for typed messages.");
				if (_status != Status.Connected)
				{
					_logger.Log(EVerbosity.Error, $"{_loggerPrefix} UWS Send called but status is {_status}");
					return false;
				}
				using (PooledArray buffer = RGMessagePacker.Pack(_factory, msg)) // the send queue takes its own reference; ours releases here
				{
					return Send(buffer);
				}
			}

			// Drain all pending inbound messages as typed objects (appends; caller clears).  Call from the main thread.
			// There is NO disposal contract here -- nothing pooled survives deserialization.  Malformed frames are logged and
			// skipped rather than disconnecting: the server is authoritative, and a malformed frame from your own server means
			// a build mismatch, where the error log is the actionable part.
			public void ReceiveAll(List<IRGMessage> messages)
			{
				if (_factory == null)
					throw new InvalidOperationException("This RGUnityWebSocket was constructed in raw mode; use the IMessageFactory constructor for typed messages.");
				_incomingMessages.MoveTo(_typedScratch);
				int drainedBytes = 0;
				try
				{
					for (int i = 0; i < _typedScratch.Count; i++)
					{
						PooledArray pa = _typedScratch[i].msg; // NOT disposed here -- the finally owns releasing every buffer exactly once
						if (_typedScratch[i].isText)
						{
							_logger.Log(EVerbosity.Error, $"{_loggerPrefix} typed ReceiveAll: text frame on a binary-only typed connection; skipped.");
							continue;
						}
						if (pa.Length < 4)
						{
							_logger.Log(EVerbosity.Error, $"{_loggerPrefix} typed ReceiveAll: runt frame ({pa.Length} bytes); skipped.");
							continue;
						}
						int         typeId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(pa.data, 0, 4));
						IRGMessage? typed  = null;
						try
						{
							typed = _factory.Deserialize(typeId, new ReadOnlySpan<byte>(pa.data, 4, pa.Length - 4));
						}
						catch (Exception e)
						{
							// A codec that THROWS on a malformed payload is the same event as one that returns null,
							// and it must be treated the same way: skip the frame, keep the batch going.  Letting it
							// escape would blow up in the game loop mid-drain and strand every remaining buffer.
							_logger.Log(EVerbosity.Error, $"{_loggerPrefix} typed ReceiveAll: factory THREW on typeId={typeId} len={pa.Length - 4}; skipped.  {e}");
							continue;
						}
						if (typed == null)
						{
							_logger.Log(EVerbosity.Error, $"{_loggerPrefix} typed ReceiveAll: factory rejected typeId={typeId} len={pa.Length - 4}; skipped.");
							continue;
						}
						messages.Add(typed);
					}
				}
				finally
				{
					// Whatever happened above, every buffer this method took ownership of is released EXACTLY once, its
					// charge is credited back, and the scratch never carries entries into the next call.  The accounting
					// lives HERE rather than in the loop so an early exit can't leave the budget over-subscribed.
					for (int i = 0; i < _typedScratch.Count; i++)
					{
						drainedBytes += QueueCharge(_typedScratch[i].msg);
						using (_typedScratch[i].msg)
						{ }
					}
					_typedScratch.Clear();
					if (drainedBytes > 0)
						Interlocked.Add(ref _queuedRecvBytes, -drainedBytes);
				}
			}

			//-------------------
			// Privates.  These calls occur on non-main-threads, so messages get queued up and you POLL them out in the Receive call above on the main thread.
			// This callback holds a reference to the PooledArray, so the consumer must dispose it to free the buffer (eventually) after it's consumed.
			private Task OnReceive(RGWebSocket rgws, PooledArray msg, bool isText)
			{
				// Inbound backlog breaker (RGWebSocketConfig.MaxRecvQueueBytes): the queue is drained by the game loop
				// calling ReceiveAll, so a loop that stalls (or a server that outruns it) grows this without limit --
				// the mirror image of the server's outbound breaker, and the difference between a hitch and an OOM on
				// a memory-tight device.  Dropping messages silently would corrupt a stateful protocol, so the
				// connection is closed instead: loud, attributable, and recoverable by reconnecting.
				int charge = QueueCharge(msg);
				int queued = Interlocked.Add(ref _queuedRecvBytes, charge);
				if (RGWebSocketConfig.MaxRecvQueueBytes > 0 && queued > RGWebSocketConfig.MaxRecvQueueBytes)
				{
					Interlocked.Add(ref _queuedRecvBytes, -charge); // this one is never queued
																	 // Latched: Close() only ASKS the socket to shut down, and every message already in flight lands here
																	 // meanwhile.  Without the latch a single trip logged once per straggler -- a log flood triggered by
																	 // exactly the traffic the breaker exists to stop.
					if (Interlocked.CompareExchange(ref _backlogTripped, 1, 0) == 0)
					{
						_lastErrorMsg = $"{_loggerPrefix} inbound queue hit {queued} bytes (limit {RGWebSocketConfig.MaxRecvQueueBytes}); ReceiveAll is not being called often enough.  Disconnecting.";
						_logger.Log(EVerbosity.Error, _lastErrorMsg);
						rgws.Close(EDisconnectReason.InboundBacklog);
					}
					return Task.CompletedTask;
				}
				msg.IncRef(); // bump the refcount since we aren't done with it yet, and RGWebSocket can decrement it without freeing the buffer
				if (_incomingMessages.Add(new wsMessage() { msg = msg, isText = isText }) == false) // queue closed under us; nobody will drain this
				{
					Interlocked.Add(ref _queuedRecvBytes, -charge);
					using (msg)
					{ } // release the IncRef we just took
				}
#if RGWS_LOGGING
				_logger.Log(EVerbosity.Debug, $"{_loggerPrefix} UWS Recv {msg.Length} bytes {(isText ? "text" : "binary")}.  IncomingMessages={_incomingMessages.Count}");
#endif
				return Task.CompletedTask;
			}

			// At this point, it's a done deal.  Both Recv and Send are completed, nothing to synchronize.  This is called at the bottom of the Send thread after Recv is completed.
			// However, it is possible that the Recv/Send threads shutdown before the RGWS constructor is even finished
			private Task OnDisconnect(RGWebSocket rgws)
			{
				_logger.Log(EVerbosity.Debug, $"{_loggerPrefix} UWS Disconnected.");
				_status = Status.ReadyToConnect;
				_disconnectCallback?.Invoke(this); // This callback needs to NOT modify any tracking structures, because it may be called as early as DURING the RGWS constructor.  Just set flags
				return Task.CompletedTask;
			}
		}
	}
}