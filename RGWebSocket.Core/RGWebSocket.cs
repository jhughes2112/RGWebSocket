#nullable enable
//-------------------
// Reachable Games
// Copyright 2023
//-------------------
// Uncomment to provide more detailed logging.  Errors from exceptions are always logged, as is the final stats for a closed socket.
//#define RGWS_LOGGING

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Logging;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		// Why a socket died.  Stamped exactly once (the FIRST cause wins) at the moment the connection's fate is decided,
		// so ops can count disconnects by cause instead of parsing error strings.
		public enum EDisconnectReason
		{
			None = 0,              // still alive (or never started)
			RemoteClose,           // the peer initiated the close handshake
			LocalClose,            // this side called Close() and the handshake ran
			TransportError,        // network death: abort, reset, receive/send exception, connect that never finished
			OutboundBackpressure,  // RGWebSocketConfig.MaxUnsentBytes circuit breaker -- peer too slow to keep up
			InboundOversize,       // RGWebSocketConfig.MaxInboundMessageBytes circuit breaker -- peer sent an abusive message
			IdleTimeout,           // WebSocketServer's idle sweep -- nothing received within RGWebSocketConfig.IdleDisconnectSeconds
			UserCodeException,     // an application callback threw
			LocalShutdown,         // Shutdown() was called while the socket was still healthy
			ProtocolError,         // the peer spoke the wrong protocol: text on a typed connection, runt frame, unknown type id, or a payload the message factory rejected
		}

		// This handles all the async/await management, speaking the websocket protocol, and manages the connection for you.
		// It's a little low-level to speak to directly, so check out ServerWebSocket and RGUnityWebSocket for writing applications.
		// There is a send thread and a recv thread that hang around until the socket closes.  The onDisconnect callback is called
		// on the Send thread (not main thread), so caution; it's also called only after all data is drained out and the socket is closed.
		public class RGWebSocket
		{
			// These can be added from any thread, and the main task will handle sending and receiving them, since websockets aren't inherently thread safe, apparently.
			private ChannelQueue<QueuedSendMsg>                _outgoing = new ChannelQueue<QueuedSendMsg>(singleReader: true, singleWriter: false);  // any thread may Send; only the send task drains.  Channel-backed: benchmarked 3-5x faster than LockingList+AsyncAutoResetEvent with ~zero steady-state allocations
			private HttpListenerContext?                       _httpContext;  // must call .Request.Close() to release a bunch of internal tracking data in the .NET lib, otherwise it leaks
			private WebSocket?                                 _webSocket;
			private Func<RGWebSocket, PooledArray, bool, Task> _onReceiveMsgCb;   // (rgws, msg, isText) -- text payloads arrive as UTF8 bytes in the pooled buffer, decode only if you need the string
			private Func<RGWebSocket, Task>                    _onDisconnectionCb;      // this must run straight through and NOT touch any tracking structures the RGWS might be added to.  This maybe called DURING the constructor!
			private ILogging                             _logger;

			private CancellationTokenSource? _cancellationTokenSource = new CancellationTokenSource();
			private Task?                    _sendTask;
			private Task?                    _recvTask;
			private string                   _actualLastErr = string.Empty;
			private bool                     _started = false;       // Start() may only be called once
			private int                      _shutdownStarted = 0;   // interlocked flag: the first Shutdown() caller does the work, everyone else awaits _shutdownDone
			private TaskCompletionSource<bool> _shutdownDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			
			// Basic metrics
			public string DisplayId           { get; private set; }  // good for troubleshooting
			public long   ConnectedAtTicks    { get; private set; }
			private int   _unsentBytes;                              // this is how you can tell if the socket is backed up
			public int    UnsentBytes  => _unsentBytes;       // accessor
			public int    SentMessages      { get; private set; }
			public long   SentBytes     { get; private set; }
			public int    RecvMessages      { get; private set; }
			public long   RecvBytes     { get; private set; }
			public long   QueuedTimeMS { get; private set; }
			public WebSocketState State        => _webSocket!.State;  // safe to read even after Shutdown
			public long   LastRecvTimestamp   { get; private set; }  // RAW Stopwatch.GetTimestamp() ticks, stamped every time ANY data arrives -- the cheapest possible liveness stamp (one clock read, one store, no unit conversion).  Compare against Stopwatch.GetTimestamp() and scale by Stopwatch.Frequency; the idle sweep does its one divide per pass, not per stamp.  Receiving is the only proof of liveness (sends just fill kernel buffers).  Deliberately not wall-clock: NTP corrections (common in containers/VMs) would make idle math jump around.
			public string LastError           { get { return _actualLastErr; } private set { _actualLastErr = value; } }

			// Why this socket died.  None while healthy; the first cause observed wins and later causes are ignored, so races
			// (e.g. Close() colliding with a remote close) resolve to whichever genuinely happened first.
			private int _disconnectReason = (int)EDisconnectReason.None;
			public EDisconnectReason        DisconnectReason             => (EDisconnectReason)_disconnectReason;
			public WebSocketCloseStatus?    RemoteCloseStatus            { get; private set; } = null;  // populated on disconnect if the peer sent a close frame
			public string?                  RemoteCloseStatusDescription { get; private set; } = null;
			private void StampDisconnectReason(EDisconnectReason reason)
			{
				Interlocked.CompareExchange(ref _disconnectReason, (int)reason, (int)EDisconnectReason.None);  // first writer wins
			}

			private void SetLastError(string error)
			{
				LastError = error;
				_logger.Log(EVerbosity.Error, LastError);
			}

			// A send/close that throws because the transport is already gone is the NORMAL end of a connection, not a
			// fault: the disconnect reason is stamped separately, and these types all mean "the socket/handle went away
			// mid-operation."  Most common is closing a socket whose peer already closed (CloseOutputAsync races the
			// remote close), or the server's listener disposing its shared IOCP handle during shutdown (ObjectDisposedException
			// on ThreadPoolBoundHandle).  None of it is actionable, so it is recorded on LastError but logged quietly --
			// only genuinely unexpected exception types keep the loud Error+stack.  (The Recv task already does this via its
			// dedicated catch (WebSocketException); this is the Send task's equivalent.)
			private static bool IsExpectedTransportTeardown(Exception e)
			{
				return e is ObjectDisposedException
					|| e is System.Net.WebSockets.WebSocketException
					|| e is System.IO.IOException
					|| e is System.Net.HttpListenerException
					|| e is System.Net.Sockets.SocketException;
			}

			// Record a benign transport-teardown on LastError without the loud Error+stack (it is expected during close/shutdown).
			private void NoteQuietDisconnect(string detail)
			{
				LastError = detail;
				_logger.Log(EVerbosity.Debug, detail);
			}

			// when we want to close, we push this through Send() so it knows to initiate closure.  Can be static because it's a sentinel, and compared by address.
			// This has a public accessor so it can be ignored when debugging memory leaks, since it's an object that appears leaked but isn't.
			static public PooledArray sCloseOutputAsync { get; private set; } = PooledArray.BorrowFromPool(1);

			// Helpful struct, reduces allocations for simple tracking.  Text and binary messages both live in pooled buffers;
			// isText just tells the websocket which frame type to send, so there's only one code path for queueing and sending.
			private struct QueuedSendMsg
			{
				public PooledArray? binMsg;   // the payload; only null in the kEmptyQSM sentinel, never in the queue
				public bool         isText;   // true = send as a text frame (payload is UTF8 bytes)
				public long         enqueuedTick;
			}

			// Constructor takes a callback to handle received messages (text arrives as UTF8 bytes with isText==true) and disconnection (which is called IN the send thread, not main thread).
			// DisplayId is only a human-readable string, uniqueId is generated here but not used internally, and is guaranteed to increment every time a websocket is created, 
			// and configuration for how to handle when the send is backed up. The cancellation source is a way for the caller to tear down the socket under any circumstances 
			// without waiting, so even if sitting blocked on a send/recv, it stops immediately.
			public RGWebSocket(HttpListenerContext? httpContext, Func<RGWebSocket, PooledArray, bool, Task> onReceiveMsg, Func<RGWebSocket, Task> onDisconnectionCb, ILogging logger, string displayId, WebSocket webSocket)
			{
				if (onReceiveMsg==null)
					throw new ArgumentNullException(nameof(onReceiveMsg));
				if (onDisconnectionCb==null)
					throw new ArgumentNullException(nameof(onDisconnectionCb));
				if (webSocket==null)
					throw new ArgumentNullException(nameof(webSocket));
				if (logger==null)
					throw new ArgumentNullException(nameof(logger));
				RGWebSocketConfig.MarkInUse();  // from here on, Configure() throws -- the pumps read the config unsynchronized

				_onReceiveMsgCb = onReceiveMsg;
				_onDisconnectionCb = onDisconnectionCb;
				_logger = logger;
				DisplayId = displayId;
				ConnectedAtTicks = DateTime.UtcNow.Ticks;
				LastRecvTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();  // a socket that never receives anything ages from its connection time
				_httpContext = httpContext;
				_webSocket = webSocket;

				// NOTE: the constructor is pure wiring -- nothing runs and no callbacks can fire until Start() is called.

#if RGWS_LOGGING
				_logger.Log(EVerbosity.Extreme, $"{DisplayId} RGWebSocket constructor");
#endif
			}

			// Kicks off the send/recv pumps.  Call this exactly once, AFTER the socket is registered wherever it needs to be tracked.
			// Because nothing happens until this is called, there is no longer any race where onDisconnection fires during construction.
			public void Start()
			{
				if (_started)
					throw new InvalidOperationException($"RGWebSocket.Start may only be called once. RGWSID={DisplayId}");
				_started = true;
				_recvTask = Task.Run(async () => await Recv(_cancellationTokenSource!.Token).ConfigureAwait(false));
				_sendTask = Task.Run(async () => await Send(_cancellationTokenSource!.Token).ConfigureAwait(false));
			}

			// This is called by this program when we want to close the websocket.
			public void Close()
			{
				// This always wakes up the send thread and tells it to close.
				Send(sCloseOutputAsync);
			}

			// Same as Close(), but records WHY -- use this when policy is doing the closing (idle sweeps, kick commands, etc)
			// so the disconnect shows up under the right cause instead of a generic LocalClose.
			public void Close(EDisconnectReason reason)
			{
				StampDisconnectReason(reason);
				Send(sCloseOutputAsync);
			}

			// Tears down the socket: cancels the pumps, waits for them to exit, then disposes everything exactly once.
			// Idempotent and thread-safe: the first caller does the work, every other caller (concurrent or later) awaits the same completion.
			// Do NOT call this from inside the onDisconnection callback -- that callback runs ON the send task, which would be waiting
			// for itself to finish.  Hand the socket to a reaper instead, the way WebSocketServer does.
			public async Task Shutdown()
			{
#if RGWS_LOGGING
				_logger.Log(EVerbosity.Extreme, $"RGWSID={DisplayId} Shutdown called.");
#endif
				if (Interlocked.Exchange(ref _shutdownStarted, 1)==0)
				{
					StampDisconnectReason(EDisconnectReason.LocalShutdown);  // no-op if the socket already died for a real reason
					try
					{
						_cancellationTokenSource!.Cancel();  // kills the Recv and Send tasks
						if (_sendTask!=null && _recvTask!=null)  // never Start()ed?  Then there are no pumps to wait for.
							await Task.WhenAll(_sendTask, _recvTask).ConfigureAwait(false);  // the pumps swallow their own exceptions, so this does not throw
					}
					finally
					{
						// Break the websocket connection and drop all the held resources.  Note we intentionally do NOT call Task.Dispose()
						// (unnecessary, and it has a history of throwing depending on task status), and we do NOT null the fields out,
						// so stragglers reading _websocketState or LastError after shutdown get sane answers instead of exceptions.
						_webSocket!.Dispose();
						try
						{
							_httpContext?.Response.Close();  // in the client/Unity case, httpContext is null.  Must be closed or the .NET internals leak tracking data.
						}
						catch (Exception)  // HttpListenerResponse is badly behaved if anything already touched it; nothing useful can be done here
						{
						}
						_cancellationTokenSource!.Dispose();  // safe: IsCancellationRequested still reads true after disposal, which is all Send() looks at
						_shutdownDone.TrySetResult(true);
#if RGWS_LOGGING
						_logger.Log(EVerbosity.Extreme, $"RGWSID={DisplayId} Shutdown completed.");
#endif
					}
				}
				await _shutdownDone.Task.ConfigureAwait(false);  // every caller returns only when teardown has genuinely finished
			}

			// Thread-friendly way to send any message to the remote client.
			// Note, this does pin the incoming msg until it's sent, which is why it's pooled.
			public void Send(PooledArray binMsg)
			{
				try
				{
					// There's a race condition where the socket is torn down but a message gets added to the queue and a refcount leaks, so we have to avoid queueing it up if the socket is canceled already
					if (_cancellationTokenSource!=null && _cancellationTokenSource.IsCancellationRequested==false)
					{
						int unsentBytes = Interlocked.Add(ref _unsentBytes, binMsg.Length);
						if (unsentBytes>RGWebSocketConfig.MaxUnsentBytes)  // slow consumer circuit breaker -- see RGWebSocketConfig.MaxUnsentBytes
						{
							SetLastError($"RGWSID={DisplayId} unsent queue hit {unsentBytes} bytes (limit {RGWebSocketConfig.MaxUnsentBytes}).  Disconnecting slow consumer.");
							StampDisconnectReason(EDisconnectReason.OutboundBackpressure);
							_cancellationTokenSource.Cancel();  // tears down both pumps; already-queued messages are drained and released by the send task's finally
							return;  // this message is NOT queued (no IncRef happened) -- the connection is already dying
						}
						binMsg.IncRef();      // because this is being queued, we don't want to let the caller reap the buffer yet
						QueuedSendMsg qsm = new QueuedSendMsg() { binMsg = binMsg, isText = false, enqueuedTick = DateTime.UtcNow.Ticks };
						_outgoing.Add(qsm);   // lock-free enqueue; the send task's WaitToReadAsync wakes on its own
					}
				}
				catch (Exception ex)
				{
					SetLastError($"SendInternalBin RGWSID={DisplayId} {ex}");
				}
			}

			// Same as above, only this one sends as a text frame.  The string is UTF8-encoded straight into a pooled buffer here,
			// so from this point on text and binary messages flow through the exact same path with no extra allocations or copies.
			public void Send(string msg)
			{
				try
				{
					// There's a race condition where the socket is torn down but a message gets added to the queue, so we have to avoid queueing it up if the socket is canceled already
					if (_cancellationTokenSource!=null && _cancellationTokenSource.IsCancellationRequested==false)
					{
						int byteCount = System.Text.Encoding.UTF8.GetByteCount(msg);
						int unsentBytes = Interlocked.Add(ref _unsentBytes, byteCount);
						if (unsentBytes>RGWebSocketConfig.MaxUnsentBytes)  // slow consumer circuit breaker -- see RGWebSocketConfig.MaxUnsentBytes
						{
							SetLastError($"RGWSID={DisplayId} unsent queue hit {unsentBytes} bytes (limit {RGWebSocketConfig.MaxUnsentBytes}).  Disconnecting slow consumer.");
							StampDisconnectReason(EDisconnectReason.OutboundBackpressure);
							_cancellationTokenSource.Cancel();  // tears down both pumps; already-queued messages are drained and released by the send task's finally
							return;  // this message is NOT queued (nothing was borrowed) -- the connection is already dying
						}
						PooledArray textMsg = PooledArray.BorrowFromPool(byteCount);  // the queue takes over this initial reference, no IncRef needed
						System.Text.Encoding.UTF8.GetBytes(msg, 0, msg.Length, textMsg.data, 0);
						QueuedSendMsg qsm = new QueuedSendMsg() { binMsg = textMsg, isText = true, enqueuedTick = DateTime.UtcNow.Ticks };
						_outgoing.Add(qsm);   // lock-free enqueue; the send task's WaitToReadAsync wakes on its own
					}
				}
				catch (Exception ex)
				{
					SetLastError($"SendInternalText RGWSID={DisplayId} {ex}");
				}
			}

			//-------------------
			// Implementation details below this line

			// This waits for data to show up, and when enough is collected, dispatch it to the app as a message buffer.
			// Frames are received DIRECTLY into the tail of a pooled buffer, so a message that fits in the initial buffer costs zero
			// managed copies.  Bigger messages grow by doubling out of the pool (borrow 2x, one BlockCopy, return the old bucket) --
			// the same doubling List<byte> used to do on the GC heap, minus the garbage and minus the final copy into a PooledArray.
			// The completed buffer IS the one handed to the application.
			private async Task Recv(CancellationToken token)
			{
				PooledArray accum = PooledArray.BorrowFromPool(RGWebSocketConfig.ReceiveBufferBytes);  // current message being assembled; also the recv target
				int accumCount = 0;  // how many bytes of accum.data are filled (accum.Length is only set just before dispatch)
				try
				{
				while (token.IsCancellationRequested==false)  // this loop is structured so the status of the ws may change and we still process everything in the incoming buffer until we hit the close.  Hence the soft exit.
				{
					switch (_webSocket!.State)
					{
						case WebSocketState.Closed:
						case WebSocketState.Aborted:
						case WebSocketState.None:
							StampDisconnectReason(EDisconnectReason.TransportError);  // no-op if a real cause (close handshake, breaker, etc) was already stamped
							_cancellationTokenSource!.Cancel();  // exits this task and kills the Send task
							break;
						case WebSocketState.CloseReceived:  // once CloseReceived, we are not allowed to ReceiveAsync on the websocket again.  The Send task replies CloseAsync to complete the handshake, then cancels everything.
							StampDisconnectReason(EDisconnectReason.RemoteClose);
							_outgoing.Add(new QueuedSendMsg());  // payload-less wake nudge: pops the send task out of WaitToReadAsync so it notices CloseReceived and sends the close reply
							try
							{
								await Task.Delay(20, token).ConfigureAwait(false);  // don't busy-spin while the send thread completes the handshake
							}
							catch (OperationCanceledException)  // not an error, flow control
							{
							}
							break;
						case WebSocketState.Connecting:
						case WebSocketState.CloseSent:  // after WE send a close, keep receiving until the peer's close reply arrives (state becomes Closed), otherwise the handshake never completes
						case WebSocketState.Open:
						{
							try
							{
								// Make sure there's room for the next chunk BEFORE waiting for data.  Growth doubles out of the pool: one
								// borrow, one copy, and the old bucket goes right back for reuse.  data.Length is always a power of two.
								if (accumCount==accum.data.Length)
								{
									if (accumCount>=RGWebSocketConfig.MaxInboundMessageBytes)  // no point borrowing a bigger bucket for a message we already know is over the limit
									{
										SetLastError($"RGWSID={DisplayId} inbound message hit {accumCount} bytes (limit {RGWebSocketConfig.MaxInboundMessageBytes}).  Disconnecting abusive sender.");
										StampDisconnectReason(EDisconnectReason.InboundOversize);
										_cancellationTokenSource!.Cancel();  // exits both pumps; the partial message is never dispatched
										break;
									}
									PooledArray bigger = PooledArray.BorrowFromPool(accum.data.Length * 2);
									Buffer.BlockCopy(accum.data, 0, bigger.data, 0, accumCount);
									using (accum) { }  // return the old bucket to the pool
									accum = bigger;
								}

								ValueWebSocketReceiveResult recvResult = await _webSocket.ReceiveAsync(new Memory<byte>(accum.data, accumCount, accum.data.Length - accumCount), token).ConfigureAwait(false);  // struct result, zero allocation per chunk
								LastRecvTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();  // ANY inbound frame proves the connection is alive; raw ticks, zero conversion cost

								// I added a separate try/catch around user handler callbacks so errors in user code would not be able to cause this Task to exit.
								if (recvResult.MessageType==WebSocketMessageType.Close)
								{
									StampDisconnectReason(EDisconnectReason.RemoteClose);  // the peer sent a close frame; the state machine handles the rest
								}
								else
								{
									// assumption is that we don't get a mixture of binary and text for a single message
									accumCount += recvResult.Count;  // the bytes were written straight into accum.data by ReceiveAsync

									// Inbound circuit breaker -- see RGWebSocketConfig.MaxInboundMessageBytes.  Without this, one endless fragmented message OOMs the server.
									if (accumCount > RGWebSocketConfig.MaxInboundMessageBytes)
									{
										SetLastError($"RGWSID={DisplayId} inbound message hit {accumCount} bytes (limit {RGWebSocketConfig.MaxInboundMessageBytes}).  Disconnecting abusive sender.");
										StampDisconnectReason(EDisconnectReason.InboundOversize);
										_cancellationTokenSource!.Cancel();  // exits both pumps; the partial message is never dispatched
									}
									// If we now have the whole message, dispatch it (synchronously).  Ignore the final close message though.
									else if (recvResult.EndOfMessage)
									{
										// keep stats
										RecvMessages++;
										RecvBytes += accumCount;

										// Tell the application about the message -- the accumulation buffer IS the message, no copy.
										accum.Length = accumCount;
										try
										{
											// Text payloads are delivered as UTF8 bytes in the pooled buffer; the callback decodes only if it actually wants a string.
											await _onReceiveMsgCb(this, accum, recvResult.MessageType==WebSocketMessageType.Text).ConfigureAwait(false);  // assume the message type doesn't change in the middle of a single message
										}
										catch (Exception e)  // handle any random exceptions that come from the logic someone might write.  If we don't do this, it silently swallows the errors and locks the websocket thread forever.
										{
											StampDisconnectReason(EDisconnectReason.UserCodeException);
											_cancellationTokenSource!.Cancel();  // exits this task and kills the Send task
											SetLastError($"USER CODE EXCEPTION CAUGHT.  Closing connection RGWSID={DisplayId} Recv: [{Enum.GetName(typeof(WebSocketState), _webSocket.State)}] {e}");
										}
										using (accum) { }  // release our reference (consumers IncRef if they kept it) and start fresh for the next message
										accum = PooledArray.BorrowFromPool(RGWebSocketConfig.ReceiveBufferBytes);
										accumCount = 0;
									}
								}
							}
							catch (OperationCanceledException)  // not an error, flow control
							{
							}
							catch (WebSocketException)  // happens when the peer vanishes without completing the handshake.  Terminal for the socket, so shut down instead of risking a hot loop on a broken pipe.
							{
								StampDisconnectReason(EDisconnectReason.TransportError);
								_cancellationTokenSource!.Cancel();  // exits this task and kills the Send task
							}
							catch (Exception ex)  // if connection is prematurely closed, we get an exception from ReceiveAsync.
							{
								StampDisconnectReason(EDisconnectReason.TransportError);
								_cancellationTokenSource!.Cancel();  // exits this task and kills the Send task
								ex.Message.ToLower();  // avoid compiler warning
#if RGWS_LOGGING
								SetLastError($"RGWSID={DisplayId} Recv: [{Enum.GetName(typeof(WebSocketState), _webSocket.State)}] {ex}");
#endif
							}
							break;
						}
					}
				}
				}
				finally
				{
					using (accum) { }  // whatever buffer was mid-assembly goes back to the pool
				}
#if RGWS_LOGGING
				_logger.Log(EVerbosity.Extreme, $"RGWS.Recv exiting {DisplayId}");
#endif
			}

			// This task will run until the socket closes or is canceled, then it exits.
			static private readonly QueuedSendMsg kEmptyQSM = new QueuedSendMsg();
			private async Task Send(CancellationToken token)
			{
				List<QueuedSendMsg> asyncQueue = new List<QueuedSendMsg>();  // this is where we copy the structs during the locking of the queue, so we can send them async outside the lock and unblock the send queue
				try
				{
					WebSocketState lastState = _webSocket!.State;
					while (token.IsCancellationRequested==false)
					{
						if (_webSocket.State!=lastState)
						{
#if RGWS_LOGGING
							_logger.Log(EVerbosity.Extreme, $"RGWSID={DisplayId} State [{Enum.GetName(typeof(WebSocketState), lastState)}] -> [{Enum.GetName(typeof(WebSocketState), _webSocket.State)}]");
#endif
							lastState = _webSocket.State;
						}
						switch (_webSocket.State)
						{
							case WebSocketState.Closed:
							case WebSocketState.Aborted:
							case WebSocketState.None:
								StampDisconnectReason(EDisconnectReason.TransportError);  // no-op if a real cause was already stamped
								_cancellationTokenSource!.Cancel();  // exits this task and kills the Recv task
								break;
							case WebSocketState.CloseSent:  // we sent the close; give the peer a bounded window to reply with its own close (Recv is still listening), then tear down
							{
								long loopsWaitingForClose = 50;  // 50 * 100 milliseconds = 5 seconds
								for (int i=0; i<loopsWaitingForClose && token.IsCancellationRequested==false && _webSocket.State==WebSocketState.CloseSent; i++)
								{
									await Task.Delay(100).ConfigureAwait(false);  // sleep a bit and see if the close handshake completes
								}
								if (token.IsCancellationRequested==false && _webSocket.State==WebSocketState.CloseSent)
								{
									_cancellationTokenSource!.Cancel();  // peer never completed the close handshake, tear it down anyway
								}
								break;
							}
							case WebSocketState.CloseReceived:
								try
								{
									StampDisconnectReason(EDisconnectReason.RemoteClose);
									await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Remote initiated close", token).ConfigureAwait(false);
									_cancellationTokenSource!.Cancel();  // exits this task and kills the Recv task
								}
								catch (OperationCanceledException)  // not an error, flow control
								{
								}
								catch (Exception ex)
								{
									_cancellationTokenSource!.Cancel();  // exits this task and kills the Recv task
									SetLastError($"Exception Caught at CloseReceived RGWSID={DisplayId} Send: [{Enum.GetName(typeof(WebSocketState), _webSocket.State)}] {ex}");
								}
								break;
							case WebSocketState.Connecting:  // do nothing while waiting to connect.
							{
								long loopsWaitingForConnection = 30;  // 30 * 100 milliseconds = 3 seconds
								for (int i=0; i<loopsWaitingForConnection && token.IsCancellationRequested==false && _webSocket.State==WebSocketState.Connecting; i++)
								{
									await Task.Delay(100).ConfigureAwait(false);  // sleep a bit and see if we have connected
								}
								if (token.IsCancellationRequested==false && _webSocket.State == WebSocketState.Connecting)
								{
									StampDisconnectReason(EDisconnectReason.TransportError);
									_cancellationTokenSource!.Cancel();  // still trying to connect, means something broke, tear it down
								}
								break;
							}
							case WebSocketState.Open:
							{
								// Bulk-drain everything currently queued into asyncQueue in one go (lock-free reads from the channel).
								_outgoing.MoveTo(asyncQueue);

								// Nothing to send?  Park until a message (or a wake nudge from the recv task) arrives, or we get canceled.
								// Then loop back around so the websocket state is re-checked before anything is sent.
								if (asyncQueue.Count==0)
								{
									try
									{
										await _outgoing.WaitToReadAsync(token).ConfigureAwait(false);  // amortized allocation-free, unlike the old AsyncAutoResetEvent
									}
									catch (OperationCanceledException)  // not an error, flow control
									{
									}
									catch (Exception ex)
									{
										StampDisconnectReason(EDisconnectReason.TransportError);
										_cancellationTokenSource!.Cancel();  // exits this task and kills the Recv task
										if (IsExpectedTransportTeardown(ex))
											NoteQuietDisconnect($"RGWSID={DisplayId} Send closed on teardown: [{Enum.GetName(typeof(WebSocketState), _webSocket.State)}] {ex.GetType().Name}: {ex.Message}");
										else
											SetLastError($"Exception caught waiting on _outgoing RGWSID={DisplayId} Send: [{Enum.GetName(typeof(WebSocketState), _webSocket.State)}] {ex}");
									}
									break;
								}

								// Now, run through the asyncQueue and send all the messages in order, removing them as we go.  Otherwise we get memory leaks.  If any exception is thrown, we are skipping the remaining messages in the asyncQueue and generally aborting the send thread.
								try
								{
									int bytesSent = 0;
									for (int i=0; i<asyncQueue.Count; i++)
									{
										QueuedSendMsg qsm = asyncQueue[i];  // this is copying to a LOCAL object, since QueuedSendMsg is a struct.
										if (qsm.binMsg==null)  // payload-less wake nudge (see Recv's CloseReceived case) -- nothing to send, the point was to break out of WaitToReadAsync
											continue;

										// it's possible the state changed during one of the messages being sent
										long deltaMillis = (long)(TimeSpan.FromTicks(DateTime.UtcNow.Ticks - qsm.enqueuedTick).TotalMilliseconds);
										QueuedTimeMS += deltaMillis;  // this is the total time this message was queued
#if RGWS_LOGGING
										_logger.Log(EVerbosity.Extreme, $"RGWSID={DisplayId} msg send queue time: {deltaMillis} ms");
#endif
										// Carefully null out the PooledArray in the asyncQueue on the off-chance we throw an exception part-way through this loop, we won't accidentally double-decrement any of them in the finally {}
										asyncQueue[i] = kEmptyQSM;
										using (qsm.binMsg)  // drops the refcount by one after this block
										{
											if (qsm.binMsg.Equals(sCloseOutputAsync))  // we want to close
											{
												StampDisconnectReason(EDisconnectReason.LocalClose);  // no-op if a policy reason (idle sweep, etc) was stamped by Close(reason)
												// Only send a close frame if the state still permits it.  CloseOutputAsync is only valid from Open
												// or CloseReceived; from any other state (the peer already closed/reset, or the transport is gone) it
												// just throws -- so skip it and let teardown finish.  This AVOIDS the most common benign teardown-race
												// exception instead of merely swallowing it; genuine races that slip through are handled by the catch.
												if (_webSocket.State==WebSocketState.Open || _webSocket.State==WebSocketState.CloseReceived)
													await _webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Locally initiated close", token).ConfigureAwait(false);
												// no self-nudge needed here: the loop only parks when the queue is empty AND the state is Open, and the state is CloseSent now

												// empty/burn the remaining messages, since we're closed now
												for (i=i+1; i<asyncQueue.Count; i++)
												{
													using (asyncQueue[i].binMsg)  // drop refcounts
													{
													}
													asyncQueue[i] = kEmptyQSM;
												}
											}
											else
											{
												await _webSocket.SendAsync(new ReadOnlyMemory<byte>(qsm.binMsg.data, 0, qsm.binMsg.Length), qsm.isText ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, token).ConfigureAwait(false);  // ValueTask overload, no per-send allocation

												// keep stats
												SentMessages++;
												SentBytes += qsm.binMsg.Length;
												bytesSent += qsm.binMsg.Length;
											}
										}
									}
									asyncQueue.Clear();
									Interlocked.Add(ref _unsentBytes, -bytesSent);  // subtract off the unsent bytes.  Interlocked is kinda slow, so we don't want to do this inside the loop.
								}
								catch (OperationCanceledException)  // not an error, flow control
								{
								}
								catch (Exception e)
								{
									StampDisconnectReason(EDisconnectReason.TransportError);
									_cancellationTokenSource!.Cancel();  // exits this task and kills the Recv task
									// CloseOutputAsync/SendAsync throwing because the peer or the listener's shared handle already
									// went away is the expected end of a connection, not a fault -- log it quietly.  Only a truly
									// unexpected exception type gets the loud Error+stack.
									if (IsExpectedTransportTeardown(e))
										NoteQuietDisconnect($"RGWSID={DisplayId} Send closed on teardown: [{Enum.GetName(typeof(WebSocketState), _webSocket.State)}] {e.GetType().Name}: {e.Message}");
									else
										SetLastError($"Exception caught in Open RGWSID={DisplayId} Send: [{Enum.GetName(typeof(WebSocketState), _webSocket.State)}] {e}");
								}
								break;
							}
						}
					}
				}
				finally
				{
#if RGWS_LOGGING
					_logger.Log(EVerbosity.Extreme, $"RGWSID={DisplayId} SendExit Beginning");
#endif

					// must make sure we flag this as a dead socket
					double totalSeconds = Math.Max(0.1, TimeSpan.FromTicks(DateTime.UtcNow.Ticks - ConnectedAtTicks).TotalSeconds);
					long avgQueueDuration = QueuedTimeMS / Math.Max(1, RecvMessages);

					// Wipe and free any unsent messages so we don't leak memory.
					_outgoing.MoveTo(asyncQueue);
					for (int i=0; i<asyncQueue.Count; i++)
					{
						using (asyncQueue[i].binMsg)  // drops the refcount by one after this block, using will ignore nulls
						{
						}
					}
					_logger.Log(EVerbosity.Debug, $"RGWSID={DisplayId} ({Enum.GetName(typeof(WebSocketState), _webSocket!.State)}) SendExit Duration: {TimeSpan.FromSeconds(totalSeconds)} Recv: {RecvMessages}/{Utilities.BytesToHumanReadable(RecvBytes)}/{Utilities.BytesToHumanReadable((long)(RecvBytes / totalSeconds))}/s Send: {SentMessages}/{Utilities.BytesToHumanReadable(SentBytes)}/{Utilities.BytesToHumanReadable((long)(SentBytes / totalSeconds))}/s AvgRcvQueueDuration: {TimeSpan.FromMilliseconds(avgQueueDuration)} Unsent: {asyncQueue.Count}");
					asyncQueue.Clear();

					// Capture the peer's close frame details (if any) while the websocket is still safe to touch -- after Shutdown
					// disposes it, these properties may throw on some implementations.
					try
					{
						RemoteCloseStatus = _webSocket.CloseStatus;
						RemoteCloseStatusDescription = _webSocket.CloseStatusDescription;
					}
					catch (Exception)  // nothing useful to do; the socket may be in a hostile state
					{
					}

					// Let the program know this socket is dead -- note this is on the Send thread, NOT main thread!!!
					// This callback should do something simple like release a reaper thread, but not task switch.
					// When reaping, always call Shutdown() first, to ensure this thread is done closing and changing status.
					try
					{
#if RGWS_LOGGING
						_logger.Log(EVerbosity.Error, $"RGWS.Send calling onDisconnectionCb {DisplayId}");
#endif
						await _onDisconnectionCb(this).ConfigureAwait(false);
					}
					catch (Exception e)
					{
						SetLastError($"Exception caught in _onDisconnectionCb RGWSID={DisplayId} {e}");
					}
				}
			}
		}
	}
}
