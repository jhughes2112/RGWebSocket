//-------------------
// Reachable Games
// Copyright 2019
//-------------------

using System;
using System.Collections.Concurrent;
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

			private ConcurrentQueue<Tuple<string, byte[]>> _incomingMessages = new ConcurrentQueue<Tuple<string, byte[]>>();

			private string                  _connectUrl;           // caching the connection params so Reconnect is possible w/o downstream users needing to know the details
			private int                     _connectTimeoutMS;
			private Dictionary<string, string> _connectHeaders = new Dictionary<string, string>();
			public  Status                  _status { get; private set; }

			private RGWebSocket             _rgws;
			private Action<string, int>     _logCb;                // if non-null, this passes back logging, the int is 0=basic, 1=noisy, 2=very noisy from the guts of the websocket.  Some performance cost so if you don't need it, leave it null.
			private string                  _lastErrorMsg = string.Empty;

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

			public UnityWebSocket(Action<string, int> loggerCb = null)
			{
				_logCb = loggerCb;
				_status = Status.ReadyToConnect;
			}

			// Forcibly disposes the RGWS
			public void Dispose()
			{
				_rgws?.Dispose();
				_rgws = null;
			}

			// Lets you specify where to connect to.
			public Task Connect(string url, int connectTimeoutMS, Dictionary<string, string> headers)
			{
				_connectUrl = url;
				_connectTimeoutMS = connectTimeoutMS;
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
					_rgws = new RGWebSocket(OnRecvTextMsg, OnRecvBinaryMsg, OnDisconnect, _logCb, uri.ToString(), wsClient);
					_logCb?.Invoke($"UWS Connected to {_connectUrl}", 1);
				}
				catch (AggregateException age)
				{
					if (age.InnerException is OperationCanceledException)
					{
						_lastErrorMsg = "Connection timed out.";
						_logCb?.Invoke(_lastErrorMsg, 0);
					}
					else if (age.InnerException is WebSocketException)
					{
						_lastErrorMsg = ((WebSocketException)age.InnerException).Message;
						_logCb?.Invoke(_lastErrorMsg, 0);
					}
					else
					{
						_lastErrorMsg = age.Message;
						_logCb?.Invoke(_lastErrorMsg, 0);
					}
					wsClient?.Dispose();  // cleanup
					_status = Status.Disconnected;
				}
				catch (Exception e)
				{
					_lastErrorMsg = e.Message;
					_logCb?.Invoke(_lastErrorMsg, 0);
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
					_logCb?.Invoke("UWS Closed.", 1);
				}
			}

			// A simple blocking way to make sure this is all torn down: Shutdown().Wait()
			public async Task Shutdown()
			{
				if (_rgws!=null)
				{
					_rgws.Close();
					_rgws.Abort(1000);
					await _rgws.Shutdown();
				}
				Dispose();  // this nulls out _rgws
				_logCb?.Invoke("UWS shutdown.", 1);
				_status = Status.ReadyToConnect;
			}

			// Returns false if data could not be sent (eg. you aren't connected or in a good status to do so)
			public bool Send(string msg)
			{
				if (_status == Status.Connected && _rgws != null && _rgws.ReadyToSend)
				{
					_rgws.Send(msg);
					_logCb?.Invoke($"UWS Sent {msg.Length} bytes", 2);
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
					_logCb?.Invoke($"UWS Sent {msg.Length} bytes", 2);
					return true;
				}
				return false;
			}

			// This is intended for you to pump it until you get a false back.  This juggles all 
			// the messages from the Recv task thread back to whatever thread you want them on.
			public bool Receive(Action<string> textMessageCb, Action<byte[]> binaryMessageCb)
			{
				Tuple<string, byte[]> recvMsg;
				if (_incomingMessages.TryDequeue(out recvMsg))
				{
					if (string.IsNullOrEmpty(recvMsg.Item1))
					{
						binaryMessageCb(recvMsg.Item2);
					}
					else
					{
						textMessageCb(recvMsg.Item1);
					}
					return true;
				}
				return false;  // nothing there
			}

			//-------------------
			// Privates.  These calls occur on non-main-threads, so messages get queued up and you POLL them out in the Receive call above on the main thread.
			private void OnRecvTextMsg(RGWebSocket rgws, string msg)
			{
				_incomingMessages.Enqueue(new Tuple<string, byte[]>(msg, null));
				_logCb?.Invoke($"UWS Recv {msg.Length} bytes txt", 2);
			}

			private void OnRecvBinaryMsg(RGWebSocket rgws, byte[] msg)
			{
				_incomingMessages.Enqueue(new Tuple<string, byte[]>(string.Empty, msg));
				_logCb?.Invoke($"UWS Recv {msg.Length} bytes bin", 2);
			}

			// At this point, it's a done deal.  Both Recv and Send are completed, nothing to synchronize.  This is called by Send after Recv is finished.
			private void OnDisconnect(RGWebSocket rgws)
			{
				_logCb?.Invoke("UWS Disconnected.", 1);
				_status = Status.Disconnected;
			}
		}
	}
}