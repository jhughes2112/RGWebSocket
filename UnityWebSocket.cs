#nullable enable
﻿//-------------------
// Reachable Games
// Copyright 2023
//-------------------
// Uncomment to provide more detailed logging.  Errors from exceptions are always logged, as is the final stats for a closed socket.
//#define RGWS_LOGGING

using System;
using System.Net.WebSockets;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using Logging;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		// This enforces the everything-happens-on-main-thread requirement to work with Unity as a game platform.  This has been built in such a way to allow
		// you to connect/close/connect multiple times without having to destroy it.  In many cases, you will want to reconnect on foregrounding and disconnection
		// (probably) happens automatically on backgrounding, so important to make it easy.
		public class UnityWebSocket
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
				public PooledArray msg;     // the payload bytes; dispose when done
				public bool        isText;  // true = UTF8 text payload
				public string Text => System.Text.Encoding.UTF8.GetString(msg.data, 0, msg.Length);  // decode helper, only pay for the string when you ask for it
			}
			private ChannelQueue<wsMessage>    _incomingMessages = new ChannelQueue<wsMessage>(singleReader: true, singleWriter: true);  // written only by the recv task, drained only by the main thread's ReceiveAll poll

			private string                     _connectUrl;           // caching the connection params so Reconnect is possible w/o downstream users needing to know the details
			private int                        _connectTimeoutMS;
			private Dictionary<string, string> _connectHeaders = new Dictionary<string, string>();
			private volatile Status            _status = Status.ReadyToConnect;  // written on the socket's send thread (OnDisconnect), read on the main thread

			private RGWebSocket?               _rgws;  // This should only be non-null when _status==Connected.
			private RGWebSocketConfig          _config;
			private ILogging                   _logger;
			private string                     _loggerPrefix = "";
			private string                     _lastErrorMsg = string.Empty;
			private Action<UnityWebSocket>     _disconnectCallback;

			//-------------------
			// Trivial accessors
			public bool IsConnected => _status == Status.Connected;
			public bool IsConnecting => _status == Status.Connecting;
			public bool IsDisconnected => _status == Status.ReadyToConnect;
			public string LastError { get { return _rgws?.LastError ?? _lastErrorMsg; } private set { _lastErrorMsg = value; } }
			public void GetStats(out int sentMsgs, out long sentBytes, out int recvMsgs, out long recvBytes)
			{
				if (_rgws!=null)
				{
					sentMsgs = _rgws.SentMessages;
					sentBytes = _rgws.SentBytes;
					recvMsgs = _rgws.RecvMessages;
					recvBytes = _rgws.RecvBytes;
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

			public UnityWebSocket(ILogging logger, string loggerPrefix, Action<UnityWebSocket> disconnectCallback, int connectTimeoutMS, RGWebSocketConfig config)
			{
				if (config==null)
					throw new ArgumentNullException(nameof(config), "Pass RGWebSocketConfig.Default if you want the defaults.");
				_logger = logger;
				_loggerPrefix = loggerPrefix;
				_disconnectCallback = disconnectCallback;
				_connectTimeoutMS = connectTimeoutMS;
				_connectUrl = string.Empty;
				_config = config;
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
					throw new InvalidOperationException("UnityWebSocket cannot Connect/Reconnect unless status is ReadyToConnect.  Call Shutdown() first.");
				if (_rgws!=null)
					Shutdown();  // the previous connection ended remotely and was never explicitly Shutdown; dispose it so Connect/Reconnect doesn't leak it

				_lastErrorMsg = string.Empty;
				Uri uri = new Uri(_connectUrl);  // I think this can throw exceptions for bad formatting?

				// Creates a websocket connection and lets you start sending or receiving messages on separate threads.
				ClientWebSocket wsClient = new ClientWebSocket();
				try
				{
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
						_logger.Log(EVerbosity.Debug, $"{_loggerPrefix} UWS Connected to {uri} http part");
					}

					_rgws = new RGWebSocket(null, OnReceive, OnDisconnect, _logger, uri.ToString(), wsClient, _config);
					_status = Status.Connected;
					_rgws.Start();  // pumps spin up only after everything is fully wired
					_logger.Log(EVerbosity.Debug, $"{_loggerPrefix} UWS Connected to {uri} rgws part");
				}
				catch (OperationCanceledException)  // await unwraps exceptions, so the timeout arrives directly (no AggregateException)
				{
					_lastErrorMsg = "Connection timed out.";
					_logger.Log(EVerbosity.Error, $"{_loggerPrefix} {_lastErrorMsg}");
					wsClient.Dispose();  // cleanup
					Shutdown();  // this just resets everything so we can try connecting again
				}
				catch (Exception e)
				{
					_lastErrorMsg = e.Message;  // WebSocketException and friends all carry a useful Message
					_logger.Log(EVerbosity.Error, $"{_loggerPrefix} {_lastErrorMsg}");
					wsClient.Dispose();  // cleanup
					Shutdown();  // this just resets everything so we can try connecting again
				}
			}

			// This is a friendly close, where we tell the other side and they shake on it.
			public void Close()
			{
				if (_status==Status.Connected)
				{
					_rgws!.Close();
					_logger.Log(EVerbosity.Debug, $"{_loggerPrefix} UWS Closed.");
				}
			}

			// A simple blocking way to make sure this is all torn down.
			public void Shutdown()
			{
				if (_rgws!=null)
				{
					RGWebSocket rgws = _rgws;  // prevent recursion here in shutdown
					_rgws = null;
					rgws.Shutdown().Wait();
					_logger.Log(EVerbosity.Debug, $"{_loggerPrefix} UWS connection reset.");
				}
				_status = Status.ReadyToConnect;
			}

			// Returns false if data could not be sent (eg. you aren't connected or in a good status to do so)
			public bool Send(string msg)
			{
				if (_status == Status.Connected)
				{
					_rgws!.Send(msg);
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
				if (_status == Status.Connected)
				{
					_rgws!.Send(msg);
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
				_incomingMessages.MoveTo(messageList);
			}

			//-------------------
			// Privates.  These calls occur on non-main-threads, so messages get queued up and you POLL them out in the Receive call above on the main thread.
			// This callback holds a reference to the PooledArray, so the consumer must dispose it to free the buffer (eventually) after it's consumed.
			private Task OnReceive(RGWebSocket rgws, PooledArray msg, bool isText)
			{
				msg.IncRef();  // bump the refcount since we aren't done with it yet, and RGWebSocket can decrement it without freeing the buffer
				_incomingMessages.Add(new wsMessage() { msg = msg, isText = isText });
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
				_disconnectCallback?.Invoke(this);  // This callback needs to NOT modify any tracking structures, because it may be called as early as DURING the RGWS constructor.  Just set flags
				return Task.CompletedTask;
			}
		}
	}
}