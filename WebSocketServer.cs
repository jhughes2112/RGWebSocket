//-------------------
// Reachable Games
// Copyright 2023
//-------------------

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using System.Diagnostics;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		// This class handles setting up the listener tasks, handling HTTP(S)/WS(S) connections, deciding if they are http or websocket and upgrading them,
		// and making the appropriate callbacks to upstream code, managing the shutdown process, etc.  Note, this class owns the actual RGWebSocket connections.
		// Anytime a HttpListener is stopped, it aborts all the websocket connections.
		public class WebSocketServer : IDisposable
		{		
			public delegate Task OnHttpRequest(HttpListenerContext httpContext);

			private int                       _listenerTasks;
			private int                       _connectionMS;
			private int                       _idleSeconds;
			private string                    _prefixURL;
			private OnHttpRequest             _httpRequestCallback;
			private OnLogDelegate             _logger;
			private IConnectionManager        _connectionManager;

			private Task                      _listenerUpdateTask = Task.CompletedTask;  // sleeps until one of the listeners finishes its work, then it creates a new one.  When _isRunning goes false, this exits.
			private HttpListener              _listener = null;  // this is the listener but ALSO manages some internal structure for all WebSocket objects.  When you call .Stop() on this, they all abort.

			// httpRequest callback allows you to handle ordinary non-websocket HTTP requests however you want, even at the same url
			public WebSocketServer(int listenerTasks, int connectionMS, int idleSeconds, string prefixURL, OnHttpRequest httpRequest, IConnectionManager connectionManager, OnLogDelegate logger)
			{
				if (logger==null)
					throw new Exception("Logger may not be null.");
				if (connectionManager == null)
					throw new Exception("ConnectionManager may not be null.");
				_listenerTasks       = listenerTasks;
				_connectionMS        = connectionMS;
				_prefixURL           = prefixURL;
				_httpRequestCallback = httpRequest;
				_logger              = logger;
				_connectionManager   = connectionManager;
				_idleSeconds         = idleSeconds;
			}

			public void Dispose()
			{
				if (_listener!=null)
					StopListening().GetAwaiter().GetResult();  // block
				_logger(ELogVerboseType.Info, $"WebSocketServer.Dispose");
			}

			public void StartListening()
			{
				if (_listener!=null)
					throw new Exception("WebSocketServer.StartListening should not be called twice without StopListening");

				// Kick off the listener update task.  It makes sure there are always _maxCount listener threads available to accept incoming connections.
				try
				{
					_listener = new HttpListener();
					_listener.Prefixes.Add(_prefixURL);
					_listener.TimeoutManager.IdleConnection = TimeSpan.FromSeconds(_idleSeconds);  // idle connections are cut after this long -- note, this doesn't affect websockets at all.
					_listener.Start();
					_listenerUpdateTask = ListenerUpdate(_listenerTasks);
					_logger(ELogVerboseType.Info, "WebSocketServer.Start");
				}
				catch (HttpListenerException e)
				{
					_logger(ELogVerboseType.Error, $"WebSocketServer.StartListening exception (usually port conflict) {e.ErrorCode} {e}");
					throw;  // rethrow it, there's nothing we can do here
				}
			}

			// Blocks until the listener task is torn down.  This ABORTS current connections.
			public async Task StopListening()
			{
				if (_listener!=null && _listener.IsListening)
				{
					_logger(ELogVerboseType.Info, "WebSocketServer.StopListening ConnectionManager.Shutdown");
					await _connectionManager.Shutdown().ConfigureAwait(false);  // disconnect all existing RGWS connections
					_logger(ELogVerboseType.Info, "WebSocketServer.StopListening Listener.Stop");
					_listener.Stop();  // this sets all WebSocket statuses = ABORTED.  However, this causes ObjectDisposedException when it doesn't Start properly.
					await _listenerUpdateTask.ConfigureAwait(false);
					_listenerUpdateTask.Dispose();
					_listenerUpdateTask = Task.CompletedTask;
					_logger(ELogVerboseType.Info, "WebSocketServer.StopListening Listener.Close");
					_listener.Close();
					_logger(ELogVerboseType.Info, "WebSocketServer.StopListening Complete");
				}
				_listener = null;
			}

			//-------------------
			// Run this task to make sure connections are always being handled for inbound requests.  By taking in members as parameters, they are not going to go null on us if the Dispose() call happens.
			private async Task ListenerUpdate(int numListenerTasks)
			{
				HashSet<Task> listenerTasks = new HashSet<Task>(numListenerTasks);

				// Create a local cancellation token source which goes away at the end of this function/task
				try
				{
					// Initialize the listener task count
					for (int i=0; i<numListenerTasks; i++)
					{
						Task<HttpListenerContext> t = _listener.GetContextAsync();
						listenerTasks.Add(t);
						_logger(ELogVerboseType.Debug, "WebSocketServer.ListenerUpdate - adding listener");
					}

					while (_listener.IsListening)  // loop forever until canceled
					{
						using (Task t = await Task.WhenAny(listenerTasks).ConfigureAwait(false))
						{
							listenerTasks.Remove(t);

							// Note, listenerTasks is used for BOTH listening and for tasks that run for websockets and regular HTTP requests
							// Which is why sometimes it is a Task<HttpListenerContext> and sometimes is just a regular Task that needs to be disposed when it completes.
							// That's also why we don't want to add new listener tasks except when the task /was/ a listener that just completed.  Otherwise the number of listener tasks will grow.
							Task<HttpListenerContext> connectTask = t as Task<HttpListenerContext>;
							if (connectTask != null)
							{
								_logger(ELogVerboseType.Debug, $"WebSocketServer.ListenerUpdate - listener handled {connectTask.Status}");

								// replace the listener task that just finished if the socket listener is still running
								if (_listener.IsListening)
								{
									Task newListener = _listener.GetContextAsync();
									listenerTasks.Add(newListener);
								}

								// If the connection was valid, go ahead and handle the request
								if (connectTask.IsCompletedSuccessfully)
								{
									// Actually handle the connection
									HttpListenerContext connectContext = connectTask.GetAwaiter().GetResult();  // this task is already complete, so it does not block
									Task connectionTask = HandleConnection(connectContext);
									listenerTasks.Add(connectionTask);
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
					_logger(ELogVerboseType.Error, $"WebSocketServer.ListenerUpdate - caught unexpected exception {e}");
				}
				finally
				{
					_logger(ELogVerboseType.Debug, "WebSocketServer.ListenerUpdate - disposing listener tasks");

					// Give them all one second to finish aborting.
					using (Task waitingForAll = Task.WhenAll(listenerTasks))
					{
						CancellationTokenSource timeout = new CancellationTokenSource(1000);
						try
						{
							// Listeners should already be in Faulted status and return immediately.
							await waitingForAll.WaitAsync(timeout.Token).ConfigureAwait(false);
						}
						catch  // eat any exceptions--we don't really care
						{
						}
					}
					_logger(ELogVerboseType.Debug, "WebSocketServer.ListenerUpdate - listener tasks dead");
				}
			}

			//-------------------
			// Task: when a connection is requested, depending on whether it's an HTTP request or WebSocket request, do different things.
			private async Task HandleConnection(HttpListenerContext httpContext)
			{
				// Allow debugging to actually happen, where you have unlimited time to check things without breaking a connection.  -1 means don't cancel over time.
				int timeoutMS = Debugger.IsAttached ? -1 : _connectionMS;
				if (httpContext.Request.IsWebSocketRequest)
				{
					// Kick off an async task to upgrade the web socket and do send/recv messaging, but fail if it takes more than a second to finish.
					try
					{
						_logger(ELogVerboseType.Info, "WebSocketServer.HandleConnection - websocket detected.  Upgrading connection.");
						using (CancellationTokenSource upgradeTimeout = new CancellationTokenSource(timeoutMS))
						{
							// The TimeSpan is supposed to be the websocket keepalives.  But does not appear to affect anything. I would disable keepalives if I could.
							HttpListenerWebSocketContext webSocketContext = await httpContext.AcceptWebSocketAsync(null, TimeSpan.FromSeconds(1)).WaitAsync(upgradeTimeout.Token);
							_logger(ELogVerboseType.Debug, "WebSocketServer.HandleConnection - websocket detected.  Upgraded.");

							// Note, we hook our own OnDisconnect before proxying it on to the ConnectionManager.  Note, due to heavy congestion and C# scheduling, it's entirely possible that this is already a closed socket, and is immediately flagged for destruction.
							RGWebSocket rgws = new RGWebSocket(httpContext, _connectionManager.OnReceiveText, _connectionManager.OnReceiveBinary, OnDisconnection, _logger, httpContext.Request.RemoteEndPoint.ToString(), webSocketContext.WebSocket);
							if (rgws.IsReadyForReaping==false)
							{
								await _connectionManager.OnConnection(rgws, httpContext).ConfigureAwait(false);
								_logger(ELogVerboseType.Debug, $"WebSocketServer.HandleConnection - websocket detected.  Upgrade completed. {rgws._displayId}");
							}
						}
					}
					catch (OperationCanceledException ex)  // timeout
					{
						_logger(ELogVerboseType.Error, $"WebSocketServer.HandleConnection - websocket upgrade timeout {ex}");
						httpContext.Response.StatusCode = 500;
						httpContext.Response.Close();  // this breaks the connection, otherwise it may linger forever
					}
					catch (Exception ex)// anything else
					{
						_logger(ELogVerboseType.Error, $"WebSocketServer.HandleConnection - websocket upgrade exception {ex}");
						httpContext.Response.StatusCode = 500;
						httpContext.Response.Close();  // this breaks the connection, otherwise it may linger forever
					}
				}
				else  // let the application specify what the HTTP response is, but we do the async write here to free up the app to do other things
				{
					try
					{
						_logger(ELogVerboseType.Debug, $"WebSocketServer.HandleConnection - normal http request {httpContext.Request.RawUrl}");
						using (CancellationTokenSource responseTimeout = new CancellationTokenSource(timeoutMS))
						{
							// Remember to set httpContext.Response.StatusCode, httpContext.Response.ContentLength64, and httpContenxtResponse.OutputStream
							await _httpRequestCallback(httpContext).ConfigureAwait(false);
						}
					}
					catch (OperationCanceledException ex)  // timeout
					{
						_logger(ELogVerboseType.Error, $"WebSocketServer.HandleConnection - http callback cancelled {ex}");
					}
					catch (Exception ex) // anything else
					{
						_logger(ELogVerboseType.Error, $"WebSocketServer.HandleConnection - http callback handler exception {ex}");
					}
					finally
					{
						httpContext.Response.Close();  // This frees all the memory associated with this connection.
					}
				}
			}

			// Add this websocket to the list of those we need to remove and unblock the cleanup thread
			private async Task OnDisconnection(RGWebSocket rgws)
			{
				_logger(ELogVerboseType.Debug, $"{rgws._displayId} OnDisconnection call.");
				try
				{
					await _connectionManager.OnDisconnect(rgws).ConfigureAwait(false);  // let the connection manager know it's disconnected now
				}
				catch (Exception e)
				{
					_logger(ELogVerboseType.Error, $"WebSocketServer.OnDisconnection Exception: {rgws._displayId} {e.Message}");
				}
				finally
				{
					_logger(ELogVerboseType.Debug, $"WebSocketServer.OnDisconnection finally {rgws._displayId}");
					try
					{
						await rgws.Shutdown().ConfigureAwait(false);  // give C# scheduler just a moment to transition tasks status from "running" to "completed", because it always looks completed in the debugger with a breakpoint here.
					}
					catch (Exception e)
					{
						_logger(ELogVerboseType.Error, $"WebSocketServer.OnDisconnection {rgws._displayId} {e}");
					}
				}
			}
		}
	}
}