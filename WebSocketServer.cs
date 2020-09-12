//-------------------
// Reachable Games
// Copyright 2019
//-------------------

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using System.Collections.Concurrent;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		// This class handles setting up the listener threads, handling HTTP(S)/WS(S) connections, deciding if they are http or websocket and upgrading them,
		// and making the appropriate callbacks to upstream code, managing the shutdown process, etc.  Note, this class owns the actual RGWebSocket connections.
		// Anytime a HttpListener is stopped, it aborts all the websocket connections.
		public class WebSocketServer : IDisposable
		{		
			public delegate int OnHttpRequest(HttpListenerContext httpContext, out byte[] outBuffer);

			private int                         _listenerThreads;
			private int                         _idleSeconds;
			private int                         _connectionMS;
			private string                      _prefixURL;
			private OnHttpRequest               _httpRequestCallback;
			private Action<string, int>         _logger;
			private Action<RGWebSocket>         _websocketConnection;  // tells the caller there is a new connection
			private Action<RGWebSocket, string> _onReceiveMsgText;
			private Action<RGWebSocket, byte[]> _onReceiveMsgBinary;
			private Action<RGWebSocket>         _onDisconnect;

			private CancellationTokenSource   _listenerRunning;     // if this is non-null, the listener thread is running
			private Task                      _listenerUpdateTask;  // sleeps until one of the listeners finishes its work, then it creates a new one.  When _isRunning goes false, this exits.
			private HttpListener              _listener;            // this is the listener but ALSO manages some internal structure for all WebSocket objects.  When you call .Stop() on this, they all abort.
			private ConcurrentDictionary<int, RGWebSocket> _websockets;  // these are live connections
			private ConcurrentDictionary<int, RGWebSocket> _disconnected;  // the disconnection callback moves them from _websockets to _disconnected
			
			private SemaphoreSlim             _disconnectionCount;
			private Task                      _cleanupTask;

			// The third parameter makes it possible to easily handle the case where a http connection turns into a websocket by simply handing it back to the caller.
			public WebSocketServer(int listenerThreads, int connectionMS, string prefixURL, OnHttpRequest httpRequest, Action<string, int> logger, Action<RGWebSocket> websocketConnection, Action<RGWebSocket, string> onReceiveMsgTextCb, Action<RGWebSocket, byte[]> onReceiveMsgBinaryCb, Action<RGWebSocket> onDisconnect, int idleSeconds)
			{
				_listenerThreads = listenerThreads;
				_idleSeconds = idleSeconds;
				_connectionMS = connectionMS;
				_prefixURL = prefixURL;
				_httpRequestCallback = httpRequest;
				_logger = logger;
				_websocketConnection = websocketConnection;
				_onReceiveMsgText = onReceiveMsgTextCb;
				_onReceiveMsgBinary = onReceiveMsgBinaryCb;
				_onDisconnect = onDisconnect;

				_listenerRunning = null;
				_listenerUpdateTask = null;
				_listener = new HttpListener();
				_listener.Prefixes.Add(prefixURL);
				_websockets = new ConcurrentDictionary<int, RGWebSocket>();
				_disconnected = new ConcurrentDictionary<int, RGWebSocket>();
			}

			public void Dispose()
			{
				_logger?.Invoke("WebSocketServer.Dispose - shutting down HttpListener, aborting all websockets", 1);
				if (_listener!=null && _listener.IsListening)
					_listener?.Stop();  // this sets all WebSocket statuses = ABORTED.  However, this causes ObjectDisposedException when it doesn't Start properly.
				_listener?.Close();
				_listener = null;
				_listenerRunning?.Dispose();
				_listenerRunning = null;
				_listenerUpdateTask?.Dispose();
				_listenerUpdateTask = null;
				_cleanupTask?.Dispose();
				_cleanupTask = null;
				_disconnectionCount?.Dispose();
				_disconnectionCount = null;
			}

			public void StartListening()
			{
				// Kick off the listener update task.  It makes sure there are always _maxCount listener threads available to accept incoming connections.
				try
				{
					_listener.Start();
				}
				catch (HttpListenerException e)
				{
					_logger?.Invoke($"WebSocketServer.StartListening exception (usually port conflict) {e.ErrorCode} {e.Message}", 0);
					throw;  // rethrow it, there's nothing we can do here
				}
				_listenerRunning = new CancellationTokenSource();
				_disconnectionCount = new SemaphoreSlim(0, 100000);
				_listenerUpdateTask = ListenerUpdate(_prefixURL, _listenerThreads, _listenerRunning.Token);
				_cleanupTask = CleanupThread(_listenerRunning.Token);
				_logger?.Invoke("WebSocketServer.Start", 1);
			}

			// Blocks until the listener thread is torn down.  This ABORTS current connections.
			public void StopListening(int milliseconds)
			{
				_logger?.Invoke("WebSocketServer.StopListening", 1);
				while (_websockets.Count>0)  // stay here until all the sockets are closed or ripped down
				{
					foreach (int uid in _websockets.Keys)
					{
						RGWebSocket rgws;
						if (_websockets.TryGetValue(uid, out rgws))
						{
							rgws.Close();
							rgws.Abort(milliseconds);  // force close after a brief delay
						}
					}
				}
				while (_websockets.Count > 0 || _disconnected.Count > 0)
					Thread.Yield();
				Console.Out.WriteLine("All connections closed and disposed.");

				if (_listenerUpdateTask != null || _cleanupTask != null)
				{
					_listener?.Stop();
					_listenerRunning?.Cancel();  // cancel the listener task, so the await completes
					_listenerUpdateTask?.Wait();
					_cleanupTask?.Wait();
				}
				_logger?.Invoke("WebSocketServer.StopListening completed", 1);
			}

			//-------------------
			// Run this thread to make sure connections are always being handled for inbound requests
			private async Task ListenerUpdate(string prefixURL, int numListenerThreads, CancellationToken token)
			{
				HashSet<Task> listenerTasks = new HashSet<Task>(numListenerThreads);

				// Create a local cancellation token source which goes away at the end of this function/task
				try
				{
					// Initialize the listener thread count
					for (int i=0; i<numListenerThreads; i++)
					{
						Task t = _listener?.GetContextAsync();
						listenerTasks.Add(t);
						_logger?.Invoke("WebSocketServer.ListenerUpdate - adding listener", 2);
					}

					// Pump the connections as they come in
					while (token.IsCancellationRequested==false)
					{
						using (Task t = await Task.WhenAny(listenerTasks).ConfigureAwait(false))
						{
							listenerTasks.Remove(t);

							Task<HttpListenerContext> connectTask = t as Task<HttpListenerContext>;
							if (connectTask != null)
							{
								_logger?.Invoke("WebSocketServer.ListenerUpdate - listener handled", 2);

								// replace the listener task that just finished.
								if (token.IsCancellationRequested == false)
								{
									Task newListener = _listener?.GetContextAsync();
									listenerTasks.Add(newListener);
								}

								// If the connection was valid, go ahead and handle the request
								if (connectTask.IsCompletedSuccessfully)
								{
									// Actually handle the connection
									listenerTasks.Add(HandleConnection(connectTask.Result));
								}
							}
						}
					}
				}
				catch (OperationCanceledException)  // if the token is cancelled, we pop to here
				{
				}
				catch (Exception e)
				{
					_logger?.Invoke($"WebSocketServer.ListenerUpdate - caught unexpected exception {e.Message}", 0);
				}
				finally
				{
					_logger?.Invoke("WebSocketServer.ListenerUpdate - disposing listener tasks", 1);

					// Convert to an array right quick, so we can make sure everything completes
					Task[] remaining = new Task[listenerTasks.Count];
					listenerTasks.CopyTo(remaining);
					listenerTasks.Clear();

					// Wait for each to drain out, so we don't cut off any tasks in progress.
					for (int i=0; i<remaining.Length; i++)
					{
						_logger?.Invoke("WebSocketServer.ListenerUpdate - waiting for "+i+"/"+remaining.Length, 1);
						try
						{
							await remaining[i].ConfigureAwait(false);
						}
						catch (OperationCanceledException)
						{
							// expected
						}
						catch (Exception)
						{
							// expected
						}
						remaining[i].Dispose();
					}
					_logger?.Invoke("WebSocketServer.ListenerUpdate - listener tasks dead", 1);
				}
			}

			// Run this thread to make sure connections are always being handled for inbound requests
			private async Task CleanupThread(CancellationToken token)
			{
				try
				{
					// this spins until there are no more sockets to dispose of.  
					// This is done on a different thread from OnDisconnect callback because disposing destroys the Send thread it is called from.
					while (token.IsCancellationRequested==false)
					{
						await _disconnectionCount.WaitAsync(token).ConfigureAwait(false);

						using (IEnumerator<KeyValuePair<int, RGWebSocket>> iter = _disconnected.GetEnumerator())
						{
							if (iter.MoveNext())  // get the first element of the collection
							{
								int uid = iter.Current.Key;  // there should always be at least one
								RGWebSocket rgws;
								if (_disconnected.TryRemove(uid, out rgws))
								{
									await rgws.Shutdown().ConfigureAwait(false);  // make sure the socket's tasks have all exited
									rgws.Dispose();
									_logger?.Invoke($"WebSocketServer.CleanupThread - deleted socket {uid}", 2);
								}
							}
						}
					}
				}
				catch (OperationCanceledException)  // if the token is cancelled, we pop to here
				{
				}
				catch (Exception e)
				{
					_logger?.Invoke($"WebSocketServer.CleanupThread - caught unexpected exception {e.Message}", 0);
				}
				finally
				{
					_logger?.Invoke("WebSocketServer.CleanupThread - exit", 1);
				}
			}

			//-------------------
			// Task: when a connection is requested, depending on whether it's an HTTP request or WebSocket request, do different things.
			private async Task HandleConnection(HttpListenerContext httpContext)
			{
				if (httpContext.Request.IsWebSocketRequest)
				{
					// Kick off an async task to upgrade the web socket and do send/recv messaging, but fail if it takes more than a second to finish.
					try
					{
						_logger?.Invoke("WebSocketServer.HandleConnection - websocket detected.  Upgrading connection.", 1);
						using (CancellationTokenSource upgradeTimeout = new CancellationTokenSource(_connectionMS))
						{
							HttpListenerWebSocketContext webSocketContext = await Task.Run(async () => { return await httpContext.AcceptWebSocketAsync(null).ConfigureAwait(false); }, upgradeTimeout.Token);
							_logger?.Invoke("WebSocketServer.HandleConnection - websocket detected.  Upgraded.", 1);

							RGWebSocket rgws = new RGWebSocket(httpContext, _onReceiveMsgText, _onReceiveMsgBinary, OnDisconnection, _logger, httpContext.Request.RemoteEndPoint.ToString(), webSocketContext.WebSocket, _idleSeconds);
							_websockets.TryAdd(rgws._uniqueId, rgws);
							_websocketConnection(rgws);
						}
					}
					catch (OperationCanceledException)  // timeout
					{
						_logger?.Invoke("WebSocketServer.HandleConnection - websocket upgrade timeout", 1);
						httpContext.Response.StatusCode = 500;
						httpContext.Response.Close();
					}
					catch // anything else
					{
						_logger?.Invoke("WebSocketServer.HandleConnection - websocket upgrade exception", 1);
						httpContext.Response.StatusCode = 500;
						httpContext.Response.Close();
					}
				}
				else  // let the application specify what the HTTP response is, but we do the async write here to free up the app to do other things
				{
					try
					{
						_logger?.Invoke("WebSocketServer.HandleConnection - normal http request", 1);
						using (CancellationTokenSource responseTimeout = new CancellationTokenSource(_connectionMS))
						{
							byte[] buffer = null;
							httpContext.Response.StatusCode = _httpRequestCallback(httpContext, out buffer);
							httpContext.Response.ContentLength64 = buffer.Length;
							await httpContext.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length, responseTimeout.Token).ConfigureAwait(false);
						}
					}
					catch (OperationCanceledException)  // timeout
					{
						_logger?.Invoke("WebSocketServer.HandleConnection - http response timeout", 1);
						httpContext.Response.StatusCode = 500;
					}
					catch // anything else
					{
						_logger?.Invoke("WebSocketServer.HandleConnection - http callback handler exception", 1);
						httpContext.Response.StatusCode = 500;
					}
					finally
					{
						httpContext.Response.Close();
					}
				}
			}

			// We capture the callback so we can manage the websocket set internally.
			private void OnDisconnection(RGWebSocket rgws)
			{
				RGWebSocket ws;
				if (_websockets.TryRemove(rgws._uniqueId, out ws))
				{
					_disconnected.TryAdd(rgws._uniqueId, rgws);
					_disconnectionCount.Release();
					_onDisconnect(rgws);  // let the caller know it's disconnected now
				}
			}
		}
	}
}