//-------------------
// Reachable Games
// Copyright 2019
//-------------------

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

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
			private ConcurrentQueue<Tuple<string, byte[], long>> _outgoing;
			private WebSocket                      _webSocket;
			private Action<RGWebSocket, string>    _onReceiveMsgTextCb;
			private Action<RGWebSocket, byte[]>    _onReceiveMsgBinaryCb;
			private Action<RGWebSocket>            _onDisconnectCallback;
			private Action<string, int>            _logger;

			private const int kRecvBufferSize     = 4096; // buffer size for receiving chunks of messages

			private SemaphoreSlim           _releaseSendThread;    // this is zero when no messages remain, non-zero when messages need to be sent
			private Task                    _sendTask;
			private Task                    _recvTask;
			private Task                    _idleTask;
			private CancellationTokenSource _cancellationTokenSource;
			private string                  _actualLastErr = string.Empty;
			private byte[]                  _recvBuffer = new byte[kRecvBufferSize];
			
			private int                     _idleDisconnectSeconds;  // If no messages are passed either way in _idleDisconnectSeconds, close the connection

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

			// Constructor takes different callbacks to handle the text/binary message and disconnection (which is called IN the send thread, not main thread).
			// DisplayId is only a human-readable string, uniqueId is generated here but not used internally, and is guaranteed to increment every time a websocket is created, 
			// and configuration for how to handle when the send is backed up. The cancellation source is a way for the caller to tear down the socket under any circumstances 
			// without waiting, so even if sitting blocked on a send/recv, it stops immediately.
			public RGWebSocket(Action<RGWebSocket, string> onReceiveMsgTextCb, Action<RGWebSocket, byte[]> onReceiveMsgBinaryCb, Action<RGWebSocket> onDisconnect, Action<string, int> onLog, string displayId, WebSocket webSocket, int idleDisconnectSeconds)
			{
				if (onReceiveMsgTextCb==null || onReceiveMsgBinaryCb==null || onDisconnect==null)
					throw new Exception("Cannot pass null in for receive callbacks or disconnection callback.");
				if (webSocket==null)
					throw new Exception("Cannot pass null in for webSocket.");

				_outgoing = new ConcurrentQueue<Tuple<string, byte[], long>>();  // this handles limiting the queue and blocking on main thread for us
				_onReceiveMsgTextCb = onReceiveMsgTextCb;
				_onReceiveMsgBinaryCb = onReceiveMsgBinaryCb;
				_onDisconnectCallback = onDisconnect;
				_logger = onLog;
				_displayId = displayId;
				_connectedAtTicks = DateTime.UtcNow.Ticks;
				_webSocket = webSocket;
				_idleDisconnectSeconds = idleDisconnectSeconds;
				_uniqueId = Interlocked.Increment(ref _wsUID);
				_cancellationTokenSource = new CancellationTokenSource();

				_releaseSendThread = new SemaphoreSlim(0, int.MaxValue);  // this is zero when no messages remain, non-zero when messages need to be sent
				_recvTask = Recv(_cancellationTokenSource.Token);
				_sendTask = Send(_cancellationTokenSource.Token);
				_idleTask = Idle(_cancellationTokenSource);
			}

			// Let the client know when the connection is open for business.  Any other setting we shouldn't be pumping new messages into it.
			public bool ReadyToSend { get { return _webSocket!=null && (_webSocket.State==WebSocketState.Open || _webSocket.State==WebSocketState.Connecting); } }

			// This is called by this program when we want to close the websocket.
			public void Close()
			{
				Send(sCloseOutputAsync);
				if (ReadyToSend==false)
					_logger?.Invoke("Attempting Close but websocket is " + _webSocket==null ? "NULL" : _webSocket.State.ToString(), 0);
//				Abort(1000);  // make sure things die when they should
			}

			// Tell the websocket to abort if it does not close on its own in the specified time.  If you aren't certain the socket has disconnected already, call this before calling Shutdown.
			public void Abort(int milliseconds)
			{
				_cancellationTokenSource?.CancelAfter(milliseconds);
			}

			// You can check this to see if the RGWS has disconnected, or you can await it until it does.
			public Task Shutdown()
			{
				return _sendTask ?? Task.CompletedTask;  // just block until the send thread tears down, which is the end of the RGWS.
			}

			// This does NOT stop the Tasks or Websocket gracefully, it just destroys them.
			public void Dispose()
			{
				// Final teardown happens here, after the caller has been told of the disconnection.
				_recvTask?.Dispose();
				_sendTask?.Dispose();
				_idleTask?.Dispose();
				_recvTask = null;
				_sendTask = null;
				_idleTask = null;

				_webSocket?.Dispose();
				_webSocket = null;  // this should never be set to null before here.

				_releaseSendThread?.Dispose();
				_releaseSendThread = null;

				_cancellationTokenSource?.Dispose();
				_cancellationTokenSource = null;
			}

			// Thread-friendly way to send any message to the remote client.
			//***** Note, this does pin the incoming byte[] until it's sent.  Should use a pool of byte[] buffers or derive a buffer that has an IDispose
			public void Send(byte[] msg)
			{
				if (msg==null)
				{
					_lastError = $"{_displayId} Send byte[] is null";
					_logger?.Invoke(_lastError, 0);
				}
				else if (ReadyToSend)
					SendInternal(new Tuple<string, byte[], long>(string.Empty, msg, DateTime.UtcNow.Ticks));
			}

			// Same as above, only this one queues up and sends as a text message.  Slightly complicates things, because we want order to be preserved, so 
			// the concurrent queue has to take two different payloads.
			public void Send(string msg)
			{
				if (msg == null)
				{
					_lastError = $"{_displayId} Send text string is null";
					_logger?.Invoke(_lastError, 0);
				}
				else if (ReadyToSend)
					SendInternal(new Tuple<string, byte[], long>(msg, null, DateTime.UtcNow.Ticks));
			}

			//-------------------
			// Implementation details below this line
			private void SendInternal(Tuple<string, byte[], long> msg)
			{
				_outgoing.Enqueue(msg);
				WakeSendThread();
			}

			// This waits for data to show up, and when enough is collected, dispatch it to the app as a message buffer.
			private async Task Recv(CancellationToken token)
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
							catch (WebSocketException wse)  // if connection is prematurely closed, we get an exception from ReceiveAsync.
							{
								exit = true;
								if (wse.InnerException!=null)
									_lastError = $"{_displayId} Recv: [{_webSocket.State.ToString()}] {wse.InnerException.Message}";
								else _lastError = $"{_displayId} Recv: [{_webSocket.State.ToString()}] {wse.Message}";
								_logger?.Invoke(_lastError, 1);

								Close();  // Queue up a close message and tell the send thread to shut down properly and clean up.
							}
							catch (System.Net.HttpListenerException ex)
							{
								exit = true;
								if (ex.InnerException != null)
									_lastError = $"{_displayId} Recv: [{_webSocket.State.ToString()}] {ex.InnerException.Message}";
								else _lastError = $"{_displayId} Recv: [{_webSocket.State.ToString()}] {ex.Message}";

								Close();  // Queue up a close message and tell the send thread to shut down properly and clean up.
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
											_onReceiveMsgBinaryCb(this, byteArray);
										}
										else
										{
											_onReceiveMsgTextCb(this, System.Text.Encoding.UTF8.GetString(byteArray));
										}
									}
									catch (Exception e)  // handle any random exceptions that come from the logic someone might write.  If we don't do this, it silently swallows the errors and locks the websocket thread forever.
									{
										exit = true;
										if (e.InnerException != null)
											_lastError = $"{_displayId} Recv: [{_webSocket.State.ToString()}] {e.InnerException.Message}";
										else
											_lastError = $"{_displayId} Recv: [{_webSocket.State.ToString()}] {e.Message}";
										_logger?.Invoke(_lastError, 1);

										Close();  // Queue up a close message and tell the send thread to shut down properly and clean up.
									}
								}
							}
							break;
						}
					}
				}

				WakeSendThread();  // when recv is canceled or we detect a the connection closed, wake the send thread so it can be exited, otw it stays locked forever
			}

			private void WakeSendThread()
			{
				try
				{
					// add one to the semaphore so it unblocks the send thread.  The count should always be the number of messages in the _outgoing queue, more or less.
					_releaseSendThread?.Release();
				}
				catch (SemaphoreFullException)  // if we want to put a max messages queued limit in, this would be the place to handle it.
				{
				}
			}

			// This task will run until the socket closes or is canceled, then it exits.
			private async Task Send(CancellationToken token)
			{
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
							catch (WebSocketException wse)
							{
								exit = true;
								_lastError = $"{_displayId} Send: [{_webSocket.State.ToString()}] {wse.Message}";
								_logger?.Invoke(_lastError, 1);
							}
							break;
						case WebSocketState.Connecting:
						case WebSocketState.Open:
						{
							Tuple<string, byte[], long> msgBytes;

							//-------------------
							// Wait until the semaphore says we should wake up.  If it's set, we just run right through.
							try
							{
								// Wait forever for a new message to be added or for the Recv task to tell us to give up due to websocket closure or abort
								await _releaseSendThread.WaitAsync(token).ConfigureAwait(false);
							}
							catch (OperationCanceledException)  // not an error, flow control
							{
								exit = true;
							}

							//-------------------
							// If there's anything to send, send it.
							if (_outgoing.TryDequeue(out msgBytes))  // keep sending until we run dry, then block
							{
								long deltaTicks = (DateTime.UtcNow.Ticks - msgBytes.Item3);
								_stats_msgQueuedTime += deltaTicks;  // this is the total time this message was queued
								_logger?.Invoke($"{_displayId} msg queue time: {TimeSpan.FromTicks(deltaTicks).TotalSeconds}", 2);

								try
								{
									if (msgBytes.Item2==null)  // is text message
									{
										byte[] rawMsgBytes = System.Text.Encoding.UTF8.GetBytes(msgBytes.Item1);
										await _webSocket.SendAsync(new ArraySegment<byte>(rawMsgBytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
						
										// keep stats
										_stats_sentMsgs++;
										_stats_sentBytes += rawMsgBytes.Length;
									}
									else
									{
										if (msgBytes.Item2.Equals(sCloseOutputAsync))  // we want to close
										{
											await _webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", token).ConfigureAwait(false);
										}
										else
										{
											await _webSocket.SendAsync(new ArraySegment<byte>(msgBytes.Item2), WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
						
											// keep stats
											_stats_sentMsgs++;
											_stats_sentBytes += msgBytes.Item2.Length;
										}
									}
								}
								catch (OperationCanceledException)  // not an error, flow control
								{
									exit = true;
								}
								catch (WebSocketException wse)
								{
									exit = true;
									_lastError = $"{_displayId} Send: [{_webSocket.State.ToString()}] {wse.Message}";
									_logger?.Invoke(_lastError, 1);
								}
							}
							break;
						}
					}
				}

				//-------------------

				try
				{
					// This waits a second on the off-chance Send exits due to an exception of some sort and Recv keeps running.
					await _recvTask;  // make sure recv is exited before we do the disconnect callback.
					
					_cancellationTokenSource.Cancel();  // if we don't cancel here, we have to wait for the idle task to completely expire, which isn't ideal.
					await _idleTask;  // make sure idle is exited too
				}
				catch (Exception e)
				{
					_logger?.Invoke($"Exception shutting down Send thread. {e.Message}", 0);
				}
				finally  // must make sure we call _onDisconnectCallback, otherwise it's an infinite loop waiting for send threads to tear down
				{
					WebSocketState finalState = _webSocket != null ? _webSocket.State : WebSocketState.None;

					// Let the program know this socket is dead -- note this is on the Send thread, NOT main thread!!!
					_onDisconnectCallback(this);

					double totalSeconds = Math.Max(0.1, TimeSpan.FromTicks(DateTime.UtcNow.Ticks - _connectedAtTicks).TotalSeconds);
					_logger?.Invoke($"{_displayId} ({finalState.ToString()}) SendExit Recv: {_stats_recvMsgs}/{Utilities.BytesToHumanReadable(_stats_recvBytes)}/{Utilities.BytesToHumanReadable((long)(_stats_recvBytes / totalSeconds))}/s Send: {_stats_sentMsgs}/{Utilities.BytesToHumanReadable(_stats_sentBytes)}/{Utilities.BytesToHumanReadable((long)(_stats_sentBytes / totalSeconds))}/s TotalMsgQTime: {TimeSpan.FromTicks(_stats_msgQueuedTime).TotalSeconds}", 0);
				}
			}

			//-------------------
			// This task simply monitors for messages being sent/received, and if that number doesn't change between delays, trigger the cancellation token.
			private async Task Idle(CancellationTokenSource tokenSource)
			{
				CancellationToken token = tokenSource.Token;  // we use this to allow other threads to kill this task

				try
				{
					for (;;)
					{
						int sends = _stats_sentMsgs;
						int recvs = _stats_recvMsgs;
						await Task.Delay(_idleDisconnectSeconds * 1000, token);
						if (_stats_sentMsgs == sends && _stats_recvMsgs == recvs)  // this is an idle connection
							break;
					}
					_logger?.Invoke($"{_displayId} Idle connection closed.", 1);
					Close();
				}
				catch (OperationCanceledException)  // not an error, flow control
				{
				}
				catch (Exception e)
				{
					_logger?.Invoke($"{_displayId} Idle task: {e.Message}", 0);
				}
			}
		}
	}
}
