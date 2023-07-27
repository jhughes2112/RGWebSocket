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
using Nito.AsyncEx;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		// This handles all the async/await management, speaking the websocket protocol, and manages the connection for you.
		// It's a little low-level to speak to directly, so check out ServerWebSocket and UnityWebSocket for writing applications.
		// There is a send thread and a recv thread that hang around until the socket closes.  The onDisconnect callback is called
		// on the Send thread (not main thread), so caution; it's also called only after all data is drained out and the socket is closed.
		public class RGWebSocket
		{
			// These can be added from any thread, and the main task will handle sending and receiving them, since websockets aren't inherently thread safe, apparently.
			private LockingList<QueuedSendMsg>           _outgoing = new LockingList<QueuedSendMsg>();  // this handles limiting the queue and blocking on main thread for us
			private HttpListenerContext                  _httpContext;  // must call .Request.Close() to release a bunch of internal tracking data in the .NET lib, otherwise it leaks
			private WebSocket                            _webSocket;
			private Func<RGWebSocket, string, Task>      _onReceiveMsgTextCb;
			private Func<RGWebSocket, PooledArray, Task> _onReceiveMsgBinaryCb;
			private Func<RGWebSocket, Task>              _onDisconnectionCb;      // this must run straight through and NOT touch any tracking structures the RGWS might be added to.  This maybe called DURING the constructor!
			private OnLogDelegate                        _logger;

			private const int kRecvBufferSize     = 4096;       // buffer size for receiving chunks of messages
			private const int kReallyBigMessage   = 1024*1024;  // we can handle messages bigger than this, but every time we have one bigger than this, we reset our internal receive array to free memory

			private AsyncAutoResetEvent     _releaseSendThread = new AsyncAutoResetEvent(false);  // this makes it easy to release the send thread when a new message is available
			private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
			private Task                    _sendTask;
			private Task                    _recvTask;
			private string                  _actualLastErr = string.Empty;
			
			// Basic metrics
			public string _displayId           { get; private set; }  // good for troubleshooting
			public long   _connectedAtTicks    { get; private set; }
			private int   _unsentBytes;                              // this is how you can tell if the socket is backed up
			public int    _stats_unsentBytes  => _unsentBytes;       // accessor
			public int    _stats_sentMsgs      { get; private set; }
			public long   _stats_sentBytes     { get; private set; }
			public int    _stats_recvMsgs      { get; private set; }
			public long   _stats_recvBytes     { get; private set; }
			public long   _stats_msgQueuedTime { get; private set; }
			public int    _websocketState      { get { return (int)_webSocket.State; } }
			public string _lastError           { get { return _actualLastErr; } private set { _actualLastErr = value; } }
			public bool   IsReadyForReaping    { get; private set; } = false;  // this is set true just before the OnDisconnect callback happens, because of the threaded nature of networking, it's useful to know it's already closed

			private void SetLastError(string error)
			{
				_lastError = error;
				_logger(ELogVerboseType.Error, _lastError);
			}

			// when we want to close, we push this through Send() so it knows to initiate closure.  Can be static because it's a sentinel, and compared by address.
			// This has a public accessor so it can be ignored when debugging memory leaks, since it's an object that appears leaked but isn't.
			static public PooledArray sCloseOutputAsync { get; private set; } = PooledArray.BorrowFromPool(1);

			// Helpful struct, reduces allocations for simple tracking
			private struct QueuedSendMsg
			{
				public string      textMsg;
				public PooledArray binMsg;
				public long        enqueuedTick;
			}

			// Constructor takes different callbacks to handle the text/binary message and disconnection (which is called IN the send thread, not main thread).
			// DisplayId is only a human-readable string, uniqueId is generated here but not used internally, and is guaranteed to increment every time a websocket is created, 
			// and configuration for how to handle when the send is backed up. The cancellation source is a way for the caller to tear down the socket under any circumstances 
			// without waiting, so even if sitting blocked on a send/recv, it stops immediately.
			public RGWebSocket(HttpListenerContext httpContext, Func<RGWebSocket, string, Task> onReceiveMsgText, Func<RGWebSocket, PooledArray, Task> onReceiveMsgBinary, Func<RGWebSocket, Task> onDisconnectionCb, OnLogDelegate onLog, string displayId, WebSocket webSocket)
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

				_recvTask = Task.Run(async () => await Recv(_cancellationTokenSource.Token).ConfigureAwait(false));
				_sendTask = Task.Run(async () => await Send(_cancellationTokenSource.Token).ConfigureAwait(false));

#if RGWS_LOGGING
				_logger(ELogVerboseType.ExtremelyVerbose, $"{_displayId} RGWebSocket constructor");
#endif
			}

			// This is called by this program when we want to close the websocket.
			public void Close()
			{
				// This always wakes up the send thread and tells it to close.				
				Send(sCloseOutputAsync);
			}

			// This is an optional call where you want to shutdown the websocket but don't want to do so via Disconnected callback.  Just await this, then call Dispose.
			public async Task Shutdown()
			{
#if RGWS_LOGGING
				_logger(ELogVerboseType.ExtremelyVerbose, $"RGWSID={_displayId} Shutdown called.");
#endif

				if (_cancellationTokenSource!=null)
				{
					// If we don't wait for the tasks to complete, it throws an exception trying to dispose them, and probably leaves them running forever.
					_cancellationTokenSource.Cancel();      // kills the Recv and Send tasks
					await Task.WhenAll(_sendTask, _recvTask).ConfigureAwait(false);

					// Stops the Tasks, breaks the websocket connection and drops all the held resources.
					// Final teardown happens here, after the caller has been told of the disconnection.
					_recvTask?.Dispose();
					_sendTask?.Dispose();
					_recvTask = null;
					_sendTask = null;
					_webSocket?.Dispose();  // never null out the _webSocket or tasks
					_webSocket = null;
					_httpContext?.Response.Close();  // in the client/Unity case, httpContext is null
					_httpContext = null;
					_cancellationTokenSource.Dispose();
					_cancellationTokenSource = null;

#if RGWS_LOGGING
					_logger(ELogVerboseType.ExtremelyVerbose, $"RGWSID={_displayId} Shutdown completed.");
#endif
				}
			}

			// Thread-friendly way to send any message to the remote client.
			// Note, this does pin the incoming msg until it's sent, which is why it's pooled.
			public void Send(PooledArray binMsg)
			{
				try
				{
					Interlocked.Add(ref _unsentBytes, binMsg.Length);
					binMsg.IncRef();      // because this is being queued, we don't want to let the caller reap the buffer yet
					QueuedSendMsg qsm = new QueuedSendMsg() { textMsg = string.Empty, binMsg = binMsg, enqueuedTick = DateTime.UtcNow.Ticks };
					_outgoing.Add(qsm);        // this automatically locks the queue on add/remove
					_releaseSendThread.Set();  // unlock the send thread since there's work for it to do
				}
				catch (Exception ex)
				{
					SetLastError($"SendInternalBin RGWSID={_displayId} {ex}");
				}
			}

			// Same as above, only this one queues up and sends as a text message.  Slightly complicates things, because we want order to be preserved, so 
			// the concurrent queue has to take two different payloads.
			public void Send(string msg)
			{
				try
				{
					Interlocked.Add(ref _unsentBytes, msg.Length);
					QueuedSendMsg qsm = new QueuedSendMsg() { textMsg = msg, binMsg = null, enqueuedTick = DateTime.UtcNow.Ticks };
					_outgoing.Add(qsm);        // this automatically locks the queue on add/remove
					_releaseSendThread.Set();  // unlock the send thread since there's work for it to do
				}
				catch (Exception ex)
				{
					SetLastError($"SendInternalText RGWSID={_displayId} {ex}");
				}
			}

			//-------------------
			// Implementation details below this line

			// This waits for data to show up, and when enough is collected, dispatch it to the app as a message buffer.
			private async Task Recv(CancellationToken token)
			{
				byte[] recvBuffer = new byte[kRecvBufferSize];
				List<byte> messageBytes = new List<byte>(kRecvBufferSize);  // start the buffer off with an allocation that is reasonable, to prevent a bunch of resizings
				while (token.IsCancellationRequested==false)  // this loop is structured so the status of the ws may change and we still process everything in the incoming buffer until we hit the close.  Hence the soft exit.
				{
					switch (_webSocket.State)
					{
						case WebSocketState.CloseReceived:  // once CloseReceived, we are not allowed to ReceiveAsync on the websocket again
						case WebSocketState.Closed:
						case WebSocketState.CloseSent:  // I think if close was sent, we should not be listening for more data on this pipe, just shut down.
						case WebSocketState.Aborted:
						case WebSocketState.None:
							_cancellationTokenSource.Cancel();  // exits this task and kills the Send task
							break;
						case WebSocketState.Connecting:
						case WebSocketState.Open:
						{
							try
							{
								WebSocketReceiveResult recvResult = await _webSocket.ReceiveAsync(new ArraySegment<byte>(recvBuffer), token).ConfigureAwait(false);

								// I added a separate try/catch around user handler callbacks so errors in user code would not be able to cause this Task to exit.
								if (recvResult!=null && recvResult.MessageType!=WebSocketMessageType.Close)
								{
									// assumption is that we don't get a mixture of binary and text for a single message
									messageBytes.AddRange(new ArraySegment<byte>(recvBuffer, 0, recvResult.Count));

									// If we now have the whole message, dispatch it (synchronously).  Ignore the final close message though.
									if (recvResult.EndOfMessage)
									{
										// keep stats
										_stats_recvMsgs++;
										_stats_recvBytes += messageBytes.Count;

										// Tell the application about the message, then reset the buffer and keep going.
										using (PooledArray byteArray = PooledArray.BorrowFromPool(messageBytes.Count))  // avoid new allocations, instead try to pull from a known-size pool of messages
										{
											messageBytes.CopyTo(0, byteArray.data, 0, messageBytes.Count);
											messageBytes.Clear();
											if (messageBytes.Capacity > kReallyBigMessage)  // Since the messageBytes array is internal to the websocket and never resizes smaller on its own, we manually do this to prevent one REALLY BIG message pinning a ton of memory until the socket closes.
											{
												messageBytes.Capacity = kRecvBufferSize;  // reset this buffer to a reasonable size
											}

											try
											{
												if (recvResult.MessageType==WebSocketMessageType.Binary)  // assume this doesn't change in the middle of a single message
												{
													await _onReceiveMsgBinaryCb(this, byteArray).ConfigureAwait(false);
												}
												else
												{
													await _onReceiveMsgTextCb(this, System.Text.Encoding.UTF8.GetString(byteArray.data, 0, byteArray.Length)).ConfigureAwait(false);
												}
											}
											catch (Exception e)  // handle any random exceptions that come from the logic someone might write.  If we don't do this, it silently swallows the errors and locks the websocket thread forever.
											{
												_cancellationTokenSource.Cancel();  // exits this task and kills the Send task
												SetLastError($"USER CODE EXCEPTION CAUGHT.  Closing connection RGWSID={_displayId} Recv: [{Enum.GetName(typeof(WebSocketState), _webSocket.State)}] {e}");
											}
										}
									}
								}
							}
							catch (OperationCanceledException)  // not an error, flow control
							{
							}
							catch (WebSocketException)  // happens when the client closes the connection without completing the handshake.  Whatever.
							{
							}
							catch (Exception ex)  // if connection is prematurely closed, we get an exception from ReceiveAsync.
							{
								_cancellationTokenSource.Cancel();  // exits this task and kills the Send task
								SetLastError($"RGWSID={_displayId} Recv: [{Enum.GetName(typeof(WebSocketState), _webSocket.State)}] {ex}");
							}
							break;
						}
					}
				}
			}

			// This task will run until the socket closes or is canceled, then it exits.
			private QueuedSendMsg kEmptyQSM = new QueuedSendMsg() { binMsg = null, textMsg = null, enqueuedTick = 0 };
			private async Task Send(CancellationToken token)
			{
				List<QueuedSendMsg> asyncQueue = new List<QueuedSendMsg>();  // this is where we copy the structs during the locking of the queue, so we can send them async outside the lock and unblock the send queue
				try
				{
					WebSocketState lastState = _webSocket.State;
					while (token.IsCancellationRequested==false)
					{
						if (_webSocket.State!=lastState)
						{
#if RGWS_LOGGING
							_logger(ELogVerboseType.ExtremelyVerbose, $"RGWSID={_displayId} State [{Enum.GetName(typeof(WebSocketState), lastState)}] -> [{Enum.GetName(typeof(WebSocketState), _webSocket.State)}]");
#endif
							lastState = _webSocket.State;
						}
						switch (_webSocket.State)
						{
							case WebSocketState.CloseSent:  // Probably not allowed to send again after sending Close once.
							case WebSocketState.Closed:
							case WebSocketState.Aborted:
							case WebSocketState.None:
								_cancellationTokenSource.Cancel();  // exits this task and kills the Recv task
								break;
							case WebSocketState.CloseReceived:
								try
								{
									await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Remote initiated close", token).ConfigureAwait(false);
									_cancellationTokenSource.Cancel();  // exits this task and kills the Recv task
								}
								catch (OperationCanceledException)  // not an error, flow control
								{
								}
								catch (Exception ex)
								{
									_cancellationTokenSource.Cancel();  // exits this task and kills the Recv task
									SetLastError($"Exception Caught at CloseReceived RGWSID={_displayId} Send: [{Enum.GetName(typeof(WebSocketState), _webSocket.State)}] {ex}");
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
									_cancellationTokenSource.Cancel();  // still trying to connect, means something broke, tear it down
								}
								break;
							}
							case WebSocketState.Open:
							{
								// This locks the _outgoing queue, moves all the entries to asyncQueue and clears _outgoing, all in one go.
								_outgoing.MoveTo(asyncQueue);

								// Now, run through the asyncQueue and send all the messages in order, removing them as we go.  Otherwise we get memory leaks.  If any exception is thrown, we are skipping the remaining messages in the asyncQueue and generally aborting the send thread.
								try
								{
									int bytesSent = 0;
									for (int i=0; i<asyncQueue.Count; i++)
									{
										QueuedSendMsg qsm = asyncQueue[i];  // this is copying to a LOCAL object, since QueuedSendMsg is a struct.

										// it's possible the state changed during one of the messages being sent
										long deltaMillis = (long)(TimeSpan.FromTicks(DateTime.UtcNow.Ticks - qsm.enqueuedTick).TotalMilliseconds);
										_stats_msgQueuedTime += deltaMillis;  // this is the total time this message was queued
#if RGWS_LOGGING
										_logger(ELogVerboseType.ExtremelyVerbose, $"RGWSID={_displayId} msg send queue time: {deltaMillis} ms");
#endif
										if (qsm.binMsg==null)  // is text message
										{
											byte[] rawMsgBytes = System.Text.Encoding.UTF8.GetBytes(qsm.textMsg);
											await _webSocket.SendAsync(new ArraySegment<byte>(rawMsgBytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
						
											// keep stats
											_stats_sentMsgs++;
											_stats_sentBytes += rawMsgBytes.Length;
											bytesSent += qsm.textMsg.Length;
										}
										else
										{
											// Carefully null out the PooledArray in the asyncQueue on the off-chance we throw an exception part-way through this loop, we won't accidentally double-decrement any of them in the finally {}
											asyncQueue[i] = kEmptyQSM;
											using (qsm.binMsg)  // drops the refcount by one after this block
											{
												if (qsm.binMsg.Equals(sCloseOutputAsync))  // we want to close
												{
													await _webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Locally initiated close", token).ConfigureAwait(false);

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
													await _webSocket.SendAsync(new ArraySegment<byte>(qsm.binMsg.data, 0, qsm.binMsg.Length), WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
						
													// keep stats
													_stats_sentMsgs++;
													_stats_sentBytes += qsm.binMsg.Length;
													bytesSent += qsm.binMsg.Length;
												}
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
									_cancellationTokenSource.Cancel();  // exits this task and kills the Recv task
									SetLastError($"Exception caught in Open RGWSID={_displayId} Send: [{Enum.GetName(typeof(WebSocketState), _webSocket.State)}] {e}");
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
								}
								catch (Exception ex)
								{
									_cancellationTokenSource.Cancel();  // exits this task and kills the Recv task
									SetLastError($"Exception caught at _releaseSendThread RGWSID={_displayId} Send: [{Enum.GetName(typeof(WebSocketState), _webSocket.State)}] {ex}");
								}
								break;
							}
						}
					}
				}
				finally
				{
#if RGWS_LOGGING
					_logger(ELogVerboseType.ExtremelyVerbose, $"RGWSID={_displayId} SendExit Beginning");
#endif
					// must make sure we flag this as a dead socket
					double totalSeconds = Math.Max(0.1, TimeSpan.FromTicks(DateTime.UtcNow.Ticks - _connectedAtTicks).TotalSeconds);
					long avgQueueDuration = _stats_msgQueuedTime / Math.Max(1, _stats_recvMsgs);

					// Wipe and free any unsent messages so we don't leak memory.
					_outgoing.MoveTo(asyncQueue);
					for (int i=0; i<asyncQueue.Count; i++)
					{
						using (asyncQueue[i].binMsg)  // drops the refcount by one after this block, using will ignore nulls
						{
						}
					}
					_logger(ELogVerboseType.Info, $"RGWSID={_displayId} ({Enum.GetName(typeof(WebSocketState), _webSocket.State)}) SendExit Duration: {TimeSpan.FromSeconds(totalSeconds)} Recv: {_stats_recvMsgs}/{Utilities.BytesToHumanReadable(_stats_recvBytes)}/{Utilities.BytesToHumanReadable((long)(_stats_recvBytes / totalSeconds))}/s Send: {_stats_sentMsgs}/{Utilities.BytesToHumanReadable(_stats_sentBytes)}/{Utilities.BytesToHumanReadable((long)(_stats_sentBytes / totalSeconds))}/s AvgRcvQueueDuration: {TimeSpan.FromMilliseconds(avgQueueDuration)} Unsent: {asyncQueue.Count}");
					asyncQueue.Clear();

					// Let the program know this socket is dead -- note this is on the Send thread, NOT main thread!!!
					// This callback should do something simple like release a reaper thread, but not task switch.
					// When reaping, always call Shutdown() first, to ensure this thread is done closing and changing status.
					try
					{
						IsReadyForReaping = true;
						await _onDisconnectionCb(this).ConfigureAwait(false);
					}
					catch (Exception e)
					{
						SetLastError($"Exception caught in _onDisconnectionCb RGWSID={_displayId} {e}");
					}
				}
			}
		}
	}
}
