//-------------------
// Reachable Games
// Copyright 2021
//-------------------

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Nito.AsyncEx;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		// This handles all the async/await management, speaking the websocket protocol, and manages the connection for you.
		// It's a little low-level to speak to directly, so check out ServerWebSocket and UnityWebSocket for writing applications.
		// There is a send thread and a recv thread that hang around until the socket closes.  The onDisconnect callback is called
		// on the Send thread (not main thread), so caution; it's also called only after all data is drained out and the socket is closed.
		public class RGWebSocket : IDisposable
		{
			// These can be added from any thread, and the main task will handle sending and receiving them, since websockets aren't inherently thread safe, apparently.
			private LockingList<QueuedSendMsg>      _outgoing = new LockingList<QueuedSendMsg>();  // this handles limiting the queue and blocking on main thread for us
			private HttpListenerContext             _httpContext;  // must call .Request.Close() to release a bunch of internal tracking data in the .NET lib, otherwise it leaks
			private WebSocket                       _webSocket;
			private Func<RGWebSocket, string, Task> _onReceiveMsgTextCb;
			private Func<RGWebSocket, byte[], Task> _onReceiveMsgBinaryCb;
			private Action<RGWebSocket>             _onDisconnectionCb;      // this must run straight through and NOT touch any tracking structures the RGWS might be added to.  This maybe called DURING the constructor!
			private Action<string, int>             _logger;

			private const int kRecvBufferSize     = 4096; // buffer size for receiving chunks of messages

			private AsyncAutoResetEvent     _releaseSendThread = new AsyncAutoResetEvent(false);  // this makes it easy to release the send thread when a new message is available
			private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
			private Task                    _sendTask;
			private Task                    _recvTask;
			private string                  _actualLastErr = string.Empty;
			private byte[]                  _recvBuffer = new byte[kRecvBufferSize];
			
			// Basic metrics
			public int    _uniqueId           { get; private set; }  // typically used by calling program to keep additional state in a dictionary
			public string _displayId          { get; private set; }  // good for troubleshooting
			public long   _connectedAtTicks   { get; private set; }
			public int    _stats_sentMsgs     { get; private set; }
			public long   _stats_sentBytes    { get; private set; }
			public int    _stats_recvMsgs     { get; private set; }
			public long   _stats_recvBytes    { get; private set; }
			public long   _stats_msgQueuedTime { get; private set; }
			public string _lastError          { get { return _actualLastErr; } private set { _actualLastErr = value; } }

			// This is used to generate globally unique id per execution instance, which can be retrieved by the caller for routing/tracking purposes as a connection id.
			static private volatile int _wsUID = 1;

			// when we want to close, we push this through Send() so it knows to initiate closure.  Can be static because it's a sentinel, and compared by address.
			static private byte[] sCloseOutputAsync = new byte[1];  

			// Helpful struct, reduces allocations for simple tracking
			private struct QueuedSendMsg
			{
				public string textMsg;
				public byte[] binMsg;
				public long   enqueuedTick;
			}

			// Constructor takes different callbacks to handle the text/binary message and disconnection (which is called IN the send thread, not main thread).
			// DisplayId is only a human-readable string, uniqueId is generated here but not used internally, and is guaranteed to increment every time a websocket is created, 
			// and configuration for how to handle when the send is backed up. The cancellation source is a way for the caller to tear down the socket under any circumstances 
			// without waiting, so even if sitting blocked on a send/recv, it stops immediately.
			public RGWebSocket(HttpListenerContext httpContext, Func<RGWebSocket, string, Task> onReceiveMsgText, Func<RGWebSocket, byte[], Task> onReceiveMsgBinary, Action<RGWebSocket> onDisconnectionCb, Action<string, int> onLog, string displayId, WebSocket webSocket)
			{
				if (onReceiveMsgText==null || onReceiveMsgBinary==null || onDisconnectionCb==null)
					throw new Exception("Cannot pass null in for callbacks.");
				if (webSocket==null)
					throw new Exception("Cannot pass null in for webSocket.");
				if (onLog==null)
					throw new Exception("Cannot pass null in for onLog.");

				_onReceiveMsgTextCb = onReceiveMsgText;
				_onReceiveMsgBinaryCb = onReceiveMsgBinary;
				_onDisconnectionCb = onDisconnectionCb;
				_logger = onLog;
				_displayId = displayId;
				_connectedAtTicks = DateTime.UtcNow.Ticks;
				_httpContext = httpContext;
				_webSocket = webSocket;
				_uniqueId = Interlocked.Increment(ref _wsUID);

				_recvTask = Recv(_cancellationTokenSource.Token);
				_sendTask = Send(_cancellationTokenSource.Token);

//				_logger($"{_displayId} RGWebSocket constructor", 4);
			}

			// Let the client know when the connection is open for business.  Any other setting we shouldn't be pumping new messages into it.
			public bool ReadyToSend { get { return _webSocket.State==WebSocketState.Open || _webSocket.State==WebSocketState.Connecting; } }
			public bool IsReadyForReaping { get; private set; } = false;

			// This is called by this program when we want to close the websocket.
			public void Close()
			{
				// This always wakes up the send thread and tells it to close.
				SendInternal(new QueuedSendMsg() { textMsg = string.Empty, binMsg = sCloseOutputAsync, enqueuedTick = DateTime.UtcNow.Ticks });
				if (ReadyToSend==false)
				{
					_lastError = $"{_displayId} Attempting Close but websocket is {_webSocket.State}";
					_logger(_lastError, 2);
				}
			}

			// This cancels the send/recv tasks and causes the disconnection callback, which eventually will call Dispose on the websocket if implemented correctly.
			private void Abort()
			{
				_cancellationTokenSource.Cancel();
			}

			// This is an optional call where you want to shutdown the websocket but don't want to do so via Disconnected callback.  Just await this, then call Dispose.
			public void Shutdown()
			{
				Abort();
				_sendTask.Wait();  // when the send task is done, it's finished tearing down
			}

			// Stops the Tasks, breaks the websocket connection and drops all the held resources.
			public void Dispose()
			{
//				_logger($"{_displayId} Dispose called.", 4);

				// Final teardown happens here, after the caller has been told of the disconnection.
				Shutdown();  // If we don't wait for the tasks to complete, it throws an exception trying to dispose them, and probably leaves them running forever.
				_recvTask.Dispose();
				_sendTask.Dispose();
				_webSocket.Dispose();  // never null out the _webSocket or tasks
				_httpContext?.Response.Close();  // in the client/Unity case, httpContext is null
				_cancellationTokenSource.Dispose();

//				_logger($"{_displayId} Dispose completed.", 4);
			}

			// Thread-friendly way to send any message to the remote client.
			//***** Note, this does pin the incoming byte[] until it's sent.  Should use a pool of byte[] buffers or derive a buffer that has an IDispose
			public void Send(byte[] msg)
			{
				if (msg==null)
				{
					_lastError = $"{_displayId} Send byte[] is null";
					_logger(_lastError, 0);
				}
				else if (ReadyToSend)
					SendInternal(new QueuedSendMsg() { textMsg = string.Empty, binMsg = msg, enqueuedTick = DateTime.UtcNow.Ticks });
			}

			// Same as above, only this one queues up and sends as a text message.  Slightly complicates things, because we want order to be preserved, so 
			// the concurrent queue has to take two different payloads.
			public void Send(string msg)
			{
				if (msg == null)
				{
					_lastError = $"{_displayId} Send text string is null";
					_logger(_lastError, 0);
				}
				else if (ReadyToSend)
					SendInternal(new QueuedSendMsg() { textMsg = msg, binMsg = null, enqueuedTick = DateTime.UtcNow.Ticks });
			}

			//-------------------
			// Implementation details below this line
			private void SendInternal(QueuedSendMsg msg)
			{
				_outgoing.Add(msg);  // this automatically locks the queue on add/remove
				_releaseSendThread.Set();  // unlock the send thread since there's work for it to do
			}

			// This waits for data to show up, and when enough is collected, dispatch it to the app as a message buffer.
			private async Task Recv(CancellationToken token)
			{
				try
				{
					List<byte> messageBytes = new List<byte>();
					bool exit = false;
					while (!exit)  // this loop is structured so the status of the ws may change and we still process everything in the incoming buffer until we hit the close.  Hence the soft exit.
					{
						switch (_webSocket.State)
						{
							case WebSocketState.CloseReceived:  // once CloseReceived, we are not allowed to ReceiveAsync on the websocket again
							case WebSocketState.Closed:
							case WebSocketState.CloseSent:  // I think if close was sent, we should not be listening for more data on this pipe, just shut down.
							case WebSocketState.Aborted:
							case WebSocketState.None:
								exit = true;
								break;
							case WebSocketState.Connecting:
							case WebSocketState.Open:
							{
								WebSocketReceiveResult recvResult = null;
								try
								{
									recvResult = await _webSocket.ReceiveAsync(new ArraySegment<byte>(_recvBuffer), token).ConfigureAwait(false);
								}
								catch (OperationCanceledException)  // not an error, flow control
								{
									exit = true;
								}
								catch (WebSocketException)  // if connection is prematurely closed, we get an exception from ReceiveAsync.
								{
									exit = true;
									Abort();  // Tear down the socket
								}
								catch (HttpListenerException)
								{
									exit = true;
									Abort();  // Tear down the socket
								}
								catch (Exception ex)  // this is actually level 0 logger
								{
									exit = true;
									if (ex.InnerException!=null)
										_lastError = $"{_displayId} Recv: [{_webSocket.State}] {ex.InnerException.Message}";
									else _lastError = $"{_displayId} Recv: [{_webSocket.State}] {ex.Message}";
									_logger(_lastError, 0);

									// There's no way we're going to shut down cleanly when the websocket is throwing exceptions.  Don't bother closing it.
									Abort();  // Tear down the socket
								}

								// I pulled this out of the try because I do NOT want this code handling exceptions for some random user callbacks.
								if (recvResult!=null && recvResult.MessageType!=WebSocketMessageType.Close)
								{
									// assumption is that we don't get a mixture of binary and text for a single message
									messageBytes.AddRange(new ArraySegment<byte>(_recvBuffer, 0, recvResult.Count));

									// If we now have the whole message, dispatch it (synchronously).  Ignore the final close message though.
									if (recvResult.EndOfMessage)
									{
										// keep stats
										_stats_recvMsgs++;
										_stats_recvBytes += messageBytes.Count;

										// Tell the application about the message, then reset the buffer and keep going.
										byte[] byteArray = messageBytes.ToArray();  // has to do an alloc and full buffer copy for ToArray()
										messageBytes.Clear();

										try
										{
											if (recvResult.MessageType==WebSocketMessageType.Binary)  // assume this doesn't change in the middle of a single message
											{
												await _onReceiveMsgBinaryCb(this, byteArray).ConfigureAwait(false);
											}
											else
											{
												await _onReceiveMsgTextCb(this, System.Text.Encoding.UTF8.GetString(byteArray)).ConfigureAwait(false);
											}
										}
//										catch (WebSocketException wse)
										catch (Exception e)  // handle any random exceptions that come from the logic someone might write.  If we don't do this, it silently swallows the errors and locks the websocket thread forever.
										{
											exit = true;
											if (e.InnerException != null)
												_lastError = $"{_displayId} Recv: [{_webSocket.State}] {e.InnerException.Message}";
											else
												_lastError = $"{_displayId} Recv: [{_webSocket.State}] {e.Message}";
											_logger(_lastError, 0);

											Abort();  // Tear down the socket
										}
									}
								}
								break;
							}
						}
					}
				}
				finally
				{
//					_logger($"{_displayId} RecvExit Beginning", 4);
					_releaseSendThread.Set();  // when recv is canceled or we detect a the connection closed, wake the send thread so it can be exited, otw it stays locked forever
				}
			}

			// This task will run until the socket closes or is canceled, then it exits.
			private async Task Send(CancellationToken token)
			{
				try
				{
					List<QueuedSendMsg> asyncQueue = new List<QueuedSendMsg>();  // this is where we copy the structs during the locking of the queue, so we can send them async outside the lock and unblock the send queue
					bool exit = false;
					while (!exit)
					{
						switch (_webSocket.State)
						{
							case WebSocketState.CloseSent:  // Probably not allowed to send again after sending Close once.
							case WebSocketState.Closed:
							case WebSocketState.Aborted:
							case WebSocketState.None:
								exit = true;
								break;
							case WebSocketState.CloseReceived:
								try
								{
									await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", token).ConfigureAwait(false);
								}
								catch (OperationCanceledException)  // not an error, flow control
								{
									exit = true;
								}
								catch (WebSocketException)
								{
									exit = true;
								}
								catch (Exception ex)
								{
									exit = true;
									_lastError = $"{_displayId} Send: [{_webSocket.State}] {ex.Message}";
									_logger(_lastError, 0);
								}
								break;
							case WebSocketState.Connecting:
							case WebSocketState.Open:
							{
								// This locks the _outgoing queue, moves all the entries to asyncQueue and clears _outgoing, all in one go.
								_outgoing.MoveTo(asyncQueue);

								// Now, run through the asyncQueue and send all the messages in order, then clear it.  If any exception is thrown, we are skipping the remaining messages in the asyncQueue and generally aborting the send thread.
								try
								{
									foreach (QueuedSendMsg qsm in asyncQueue)
									{
										long deltaMillis = (long)(TimeSpan.FromTicks(DateTime.UtcNow.Ticks - qsm.enqueuedTick).TotalMilliseconds);
										_stats_msgQueuedTime += deltaMillis;  // this is the total time this message was queued
//										_logger($"{_displayId} msg send queue time: {deltaMillis} ms", 4);

										if (qsm.binMsg==null)  // is text message
										{
											byte[] rawMsgBytes = System.Text.Encoding.UTF8.GetBytes(qsm.textMsg);
											await _webSocket.SendAsync(new ArraySegment<byte>(rawMsgBytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
						
											// keep stats
											_stats_sentMsgs++;
											_stats_sentBytes += rawMsgBytes.Length;
										}
										else
										{
											if (qsm.binMsg.Equals(sCloseOutputAsync))  // we want to close
											{
												await _webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", token).ConfigureAwait(false);
											}
											else
											{
												await _webSocket.SendAsync(new ArraySegment<byte>(qsm.binMsg), WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
						
												// keep stats
												_stats_sentMsgs++;
												_stats_sentBytes += qsm.binMsg.Length;
											}
										}
									}
									asyncQueue.Clear();
								}
								catch (OperationCanceledException)  // not an error, flow control
								{
									exit = true;
									break;
								}
//								catch (WebSocketException wse)
								catch (Exception e)
								{
									exit = true;
									_lastError = $"{_displayId} Send: [{_webSocket.State}] {e.Message}";
									_logger(_lastError, 1);
									break;
								}

								//-------------------
								// Wait until the AsyncAutoResetEvent is says we should wake up.  If it's set, we just run right through.
								try
								{
									// Wait forever for a new message to be added or for the Recv task to tell us to give up due to websocket closure or abort
									await _releaseSendThread.WaitAsync(token).ConfigureAwait(false);
								}
								catch (OperationCanceledException)  // not an error, flow control
								{
									exit = true;
								}
								break;
							}
						}
					}
				}
				finally
				{
					try
					{
//						_logger($"{_displayId} SendExit Beginning", 4);

						// This waits a second on the off-chance Send exits due to an exception of some sort and Recv keeps running.
						await _recvTask.ConfigureAwait(false);  // make sure recv is exited before we do the disconnect callback.
					}
					catch (Exception e)
					{
						_logger($"Exception while shutting down Send thread. {e.Message}", 0);
					}
					finally  // must make sure we flag this as a dead socket
					{
						IsReadyForReaping = true;
						double totalSeconds = Math.Max(0.1, TimeSpan.FromTicks(DateTime.UtcNow.Ticks - _connectedAtTicks).TotalSeconds);
						long avgQueueDuration = _stats_msgQueuedTime / Math.Max(1, _stats_recvMsgs);
						_logger($"{_displayId} ({_webSocket.State}) SendExit Duration: {TimeSpan.FromSeconds(totalSeconds)} Recv: {_stats_recvMsgs}/{Utilities.BytesToHumanReadable(_stats_recvBytes)}/{Utilities.BytesToHumanReadable((long)(_stats_recvBytes / totalSeconds))}/s Send: {_stats_sentMsgs}/{Utilities.BytesToHumanReadable(_stats_sentBytes)}/{Utilities.BytesToHumanReadable((long)(_stats_sentBytes / totalSeconds))}/s AvgRcvQueueDuration: {TimeSpan.FromMilliseconds(avgQueueDuration)}", 1);

						// Let the program know this socket is dead -- note this is on the Send thread, NOT main thread!!!
						// This callback should do something simple like release a reaper thread, but not task switch.
						// When reaping, always call Shutdown() first, to ensure this thread is done closing and changing status.
						_onDisconnectionCb(this);
					}
				}
			}
		}
	}
}
