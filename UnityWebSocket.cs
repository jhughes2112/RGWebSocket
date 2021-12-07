//-------------------
// Reachable Games
// Copyright 2019
//-------------------

using System;
using System.Net.WebSockets;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		// This enforces the everything-happens-on-main-thread requirement to work with Unity as a game platform.  This has been built in such a way to allow
		// you to connect/close/connect multiple times without having to destroy it.  In many cases, you will want to reconnect on foregrounding and disconnection
		// (probably) happens automatically on backgrounding, so important to make it easy.
		public class UnityWebSocket : IDisposable
		{
			// This tracks the status of RGWS, which is hard to determine directly from inspection due to async operations.
			public enum Status
			{
				ReadyToConnect,
				Connecting,
				Connected,
				Disconnected
			}

			// Supports strings or binary messages on the websocket.  If binMsg is null, it's a string.
			public struct wsMessage
			{
				public string stringMsg;
				public byte[] binMsg;
			}
			private LockingList<wsMessage>     _incomingMessages = new LockingList<wsMessage>();

			private string                     _connectUrl;           // caching the connection params so Reconnect is possible w/o downstream users needing to know the details
			private int                        _connectTimeoutMS;
			private Dictionary<string, string> _connectHeaders = new Dictionary<string, string>();
			public  Status                     _status { get; private set; }
			private int                        _idleSeconds;          // if nothing happens in this period of time, disconnect

			private RGWebSocket                _rgws;  // This should only be non-null when _status==Connected.
			private Action<string, int>        _logger;
			private string                     _lastErrorMsg = string.Empty;
			private Action<UnityWebSocket>     _disconnectCallback;

			//-------------------
			// Trivial accessors
			public bool IsConnected => (_rgws!=null && _rgws.ReadyToSend);
			public string LastError { get { return _rgws?._lastError ?? _lastErrorMsg; } private set { _lastErrorMsg = value; } }
			public void GetStats(out int sentMsgs, out long sentBytes, out int recvMsgs, out long recvBytes)
			{
				if (_rgws!=null)
				{
					sentMsgs = _rgws._stats_sentMsgs;
					sentBytes = _rgws._stats_sentBytes;
					recvMsgs = _rgws._stats_recvMsgs;
					recvBytes = _rgws._stats_recvBytes;
				}
				else
				{
					sentMsgs = 0;
					sentBytes = 0;
					recvMsgs = 0;
					recvBytes = 0;
				}
			}

			//-------------------

			public UnityWebSocket(Action<string, int> logger, Action<UnityWebSocket> disconnectCallback, int connectTimeoutMS, int idleSeconds)
			{
				_logger = logger;
				_disconnectCallback = disconnectCallback;
				_status = Status.ReadyToConnect;
				_connectTimeoutMS = connectTimeoutMS;
				_idleSeconds = idleSeconds;
			}

			// Forcibly disposes the RGWS
			public void Dispose()
			{
				_rgws?.Dispose();
				_rgws = null;
			}

			// Lets you specify where to connect to.
			public Task Connect(string url, Dictionary<string, string> headers)
			{
				_connectUrl = url;
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
				if (_status!=Status.ReadyToConnect)
					throw new Exception("Not in status=ReadyToConnect.");

				_lastErrorMsg = string.Empty;
				Uri uri = new Uri(_connectUrl);  // I think this can throw exceptions for bad formatting?

				// Creates a websocket connection and lets you start sending or receiving messages on separate threads.
				ClientWebSocket wsClient = null;
				try
				{
					wsClient = new ClientWebSocket();
					wsClient.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;  // disable the keepalive ping/pong on websocket protocol
					using (CancellationTokenSource connectTimeout = new CancellationTokenSource(_connectTimeoutMS))
					{
						// Apply all the headers that were passed in.
						foreach (KeyValuePair<string, string> kvp in _connectHeaders)
						{
							wsClient.Options.SetRequestHeader(kvp.Key, kvp.Value);
						}

						_status = Status.Connecting;
						await wsClient.ConnectAsync(uri, connectTimeout.Token).ConfigureAwait(false);
					}

					_status = Status.Connected;
					_rgws = new RGWebSocket(null, OnReceiveText, OnReceiveBinary, OnDisconnect, _logger, uri.ToString(), wsClient);
					_logger($"UWS Connected to {uri}", 1);
				}
				catch (AggregateException age)
				{
					if (age.InnerException is OperationCanceledException)
					{
						_lastErrorMsg = "Connection timed out.";
						_logger(_lastErrorMsg, 0);
					}
					else if (age.InnerException is WebSocketException)
					{
						_lastErrorMsg = ((WebSocketException)age.InnerException).Message;
						_logger(_lastErrorMsg, 0);
					}
					else
					{
						_lastErrorMsg = age.Message;
						_logger(_lastErrorMsg, 0);
					}
					wsClient?.Dispose();  // cleanup
					_status = Status.Disconnected;
				}
				catch (Exception e)
				{
					_lastErrorMsg = e.Message;
					_logger(_lastErrorMsg, 0);
					wsClient?.Dispose();  // cleanup
					_status = Status.Disconnected;
				}
			}

			// This is a friendly close, where we tell the other side and they shake on it.
			public void Close()
			{
				if (_status==Status.Connected && _rgws!=null && _rgws.ReadyToSend)
				{
					_rgws.Close();
					_logger("UWS Closed.", 1);
				}
			}

			// A simple blocking way to make sure this is all torn down.
			public void Shutdown()
			{
				if (_rgws!=null)
				{
					_rgws.Shutdown();
					_rgws.Dispose();
					_rgws = null;
				}
				_logger("UWS shutdown.", 1);
				_status = Status.ReadyToConnect;
			}

			// Returns false if data could not be sent (eg. you aren't connected or in a good status to do so)
			public bool Send(string msg)
			{
				if (_status == Status.Connected && _rgws != null && _rgws.ReadyToSend)
				{
					_rgws.Send(msg);
					_logger($"UWS Sent {msg.Length} bytes", 3);
					return true;
				}
				return false;
			}

			// Returns false if data could not be sent (eg. you aren't connected or in a good status to do so)
			public bool Send(byte[] msg)
			{
				if (_status == Status.Connected && _rgws != null && _rgws.ReadyToSend)
				{
					_rgws.Send(msg);
					_logger($"UWS Sent {msg.Length} bytes", 3);
					return true;
				}
				return false;
			}

			// This is intended for you to grab all the messages that have been sent, in bulk and from the main thread, like in an MonoBehaviour.Update() method.
			public void ReceiveAll(List<wsMessage> messageList)
			{
				// Take the whole set of incoming messages, lock it, then move it to messageList and clear it out
				_incomingMessages.MoveTo(messageList);
			}

			//-------------------
			// Privates.  These calls occur on non-main-threads, so messages get queued up and you POLL them out in the Receive call above on the main thread.
			private Task OnReceiveText(RGWebSocket rgws, string msg)
			{
				_incomingMessages.Add(new wsMessage() { stringMsg = msg, binMsg = null });
				_logger($"UWS Recv {msg.Length} bytes txt", 3);
				return Task.CompletedTask;
			}

			private Task OnReceiveBinary(RGWebSocket rgws, byte[] msg)
			{
				_incomingMessages.Add(new wsMessage() { stringMsg = string.Empty, binMsg = msg });
				_logger($"UWS Recv {msg.Length} bytes bin", 3);
				return Task.CompletedTask;
			}

			// At this point, it's a done deal.  Both Recv and Send are completed, nothing to synchronize.  This is called at the bottom of the Send thread after Recv is completed.
			// However, it is possible that the Recv/Send threads shutdown before the RGWS constructor is even finished 
			private void OnDisconnect(RGWebSocket rgws)
			{
				_logger("UWS Disconnected.", 1);
				_status = Status.Disconnected;
				_disconnectCallback?.Invoke(this);  // This callback needs to NOT modify any tracking structures, because it may be called as early as DURING the RGWS constructor.  Just set flags
			}
		}
	}
}