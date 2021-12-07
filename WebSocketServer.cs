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
using System.Diagnostics;
using Nito.AsyncEx;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		// This class handles setting up the listener threads, handling HTTP(S)/WS(S) connections, deciding if they are http or websocket and upgrading them,
		// and making the appropriate callbacks to upstream code, managing the shutdown process, etc.  Note, this class owns the actual RGWebSocket connections.
		// Anytime a HttpListener is stopped, it aborts all the websocket connections.
		public class WebSocketServer : IDisposable
		{		
			public delegate Task OnHttpRequest(HttpListenerContext httpContext);

			private int                       _listenerThreads;
			private TimeSpan                  _idleSeconds;
			private int                       _connectionMS;
			private string                    _prefixURL;
			private OnHttpRequest             _httpRequestCallback;
			private Action<string, int>       _logger;
			private IConnectionManager        _connectionManager;

			private Task                      _listenerUpdateTask = null;      // sleeps until one of the listeners finishes its work, then it creates a new one.  When _isRunning goes false, this exits.
			private HttpListener              _listener = null;  // this is the listener but ALSO manages some internal structure for all WebSocket objects.  When you call .Stop() on this, they all abort.
			private ConcurrentDictionary<int, RGWebSocket> _websockets   = new ConcurrentDictionary<int, RGWebSocket>();  // these are live connections
			private LockingList<RGWebSocket>  _disconnected = new LockingList<RGWebSocket>();     // the disconnection callback moves them from _websockets to _disconnected
			
			private AsyncAutoResetEvent       _cleanupSocket = new AsyncAutoResetEvent();  // Whenever this is triggered, there's at least one websocket to clean up
			private CancellationTokenSource   _cleanupRunning = null;
			private Task                      _cleanupTask = null;

			// httpRequest callback allows you to handle ordinary non-websocket HTTP requests however you want, even at the same url
			public WebSocketServer(int listenerThreads, int connectionMS, int idleSeconds, string prefixURL, OnHttpRequest httpRequest, IConnectionManager connectionManager, Action<string, int> logger)
			{
				if (logger==null)
					throw new Exception("Logger may not be null.");
				if (connectionManager == null)
					throw new Exception("ConnectionManager may not be null.");
				_listenerThreads = listenerThreads;
				_idleSeconds = TimeSpan.FromSeconds(idleSeconds);  // This is actually the duration between keepalive messages automatically sent by websocket library.
				_connectionMS = connectionMS;
				_prefixURL = prefixURL;
				_httpRequestCallback = httpRequest;
				_logger = logger;
				_connectionManager = connectionManager;

				_listener = new HttpListener();
				_listener.Prefixes.Add(_prefixURL);
				_listener.TimeoutManager.IdleConnection = _idleSeconds;  // idle connections are cut after this long
			}

			public void Dispose()
			{
				_logger($"WebSocketServer.Dispose - shutting down HttpListener, aborting all websockets (Still Listening? {_listener.IsListening})", 2);
				if (_listener.IsListening)
					StopListening();
				_listener.Close();
			}

			public void StartListening()
			{
				// Kick off the listener update task.  It makes sure there are always _maxCount listener threads available to accept incoming connections.
				try
				{
					_listener.Start();
					_listenerUpdateTask = ListenerUpdate(_listenerThreads);
					_cleanupRunning = new CancellationTokenSource();  // have to create this on StartListening, otherwise if it was ever cancelled it would always be cancelled
					_cleanupTask = CleanupThread(_cleanupRunning.Token);  // since RGWebSocket can't dispose of itself, we do it out here in a single thread that handles cleanup
					_logger("WebSocketServer.Start", 2);
				}
				catch (HttpListenerException e)
				{
					_logger($"WebSocketServer.StartListening exception (usually port conflict) {e.ErrorCode} {e.Message}", 0);
					throw;  // rethrow it, there's nothing we can do here
				}
			}

			// Blocks until the listener thread is torn down.  This ABORTS current connections.
			public void StopListening()
			{
				_logger("WebSocketServer.StopListening", 2);
				_listener.Stop();  // this sets all WebSocket statuses = ABORTED.  However, this causes ObjectDisposedException when it doesn't Start properly.
				_listenerUpdateTask.Wait();
				_listenerUpdateTask.Dispose();
				_listenerUpdateTask = null;

				foreach (KeyValuePair<int, RGWebSocket> kvp in _websockets)  // tell all the websockets to close, now that we aren't accepting any new ones
				{
					kvp.Value.Close();
				}
				while (_websockets.Count > 0 || _disconnected.Count > 0)
					Thread.Yield();

				// Now that all the websockets are shutdown and disposed of, kill the cleanup task
				_cleanupRunning.Cancel();
				_cleanupTask.Wait();
				_cleanupRunning.Dispose();  // once CleanupTask is finished, we can dispose of this cancellation token source.
				_cleanupRunning = null;
				_cleanupTask.Dispose();
				_cleanupTask = null;

				_logger("WebSocketServer.StopListening completed", 2);
			}

			//-------------------
			// Run this thread to make sure connections are always being handled for inbound requests.  By taking in members as parameters, they are not going to go null on us if the Dispose() call happens.
			private async Task ListenerUpdate(int numListenerThreads)
			{
				HashSet<Task> listenerTasks = new HashSet<Task>(numListenerThreads);

				// Create a local cancellation token source which goes away at the end of this function/task
				try
				{
					// Initialize the listener thread count
					for (int i=0; i<numListenerThreads; i++)
					{
						Task<HttpListenerContext> t = _listener.GetContextAsync();
						listenerTasks.Add(t);
						_logger("WebSocketServer.ListenerUpdate - adding listener", 3);
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
								_logger($"WebSocketServer.ListenerUpdate - listener handled {connectTask.Status}", 3);

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
					_logger($"WebSocketServer.ListenerUpdate - caught unexpected exception {e.Message}", 0);
				}
				finally
				{
					_logger("WebSocketServer.ListenerUpdate - disposing listener tasks", 3);

					int count = listenerTasks.Count;
					int i = 0;
					foreach (Task t in listenerTasks)
					{
						// Wait for each to drain out, so we don't cut off any tasks in progress.
						i++;
						_logger($"WebSocketServer.ListenerUpdate - waiting for {i}/{count}", 3);
						using (t)
						{
							try
							{
								CancellationTokenSource timeout = new CancellationTokenSource(1000);
								await t.WaitAsync(timeout.Token).ConfigureAwait(false);  // let each open handler a second to complete its work.  Listeners should already be in Faulted status and return immediately.
							}
							catch  // eat any exceptions--we don't really care
							{
							}
						}
					}
					_logger("WebSocketServer.ListenerUpdate - listener tasks dead", 3);
				}
			}

			// Run this thread to make sure connections are always being handled for inbound requests.
			private async Task CleanupThread(CancellationToken token)
			{
				try
				{
					// This is done on a different thread from OnDisconnect callback because disposing destroys the Send thread at the same time while it's running from there.
					List<RGWebSocket> toDisconnect = new List<RGWebSocket>();
					for (;;)
					{
						await _cleanupSocket.WaitAsync(token).ConfigureAwait(false);  // this throws OperationCanceledException when it's time to cancel the thread

						// Move this locked structure's content into our local one so we can use async/await on it.  That doesn't mix well with lock() calls.
						_disconnected.MoveTo(toDisconnect);
						foreach (RGWebSocket rgws in toDisconnect)
						{
							if (_websockets.TryRemove(rgws._uniqueId, out _))  // don't try to delete the RGWS until it's completed its constructor and been fully added to the tracking structures.  Sometimes threads get out of order on startup and the socket is dead due to timeouts before it is ever alive.
							{
								try
								{
									await _connectionManager.OnDisconnect(rgws).ConfigureAwait(false);  // let the connection manager know it's disconnected now
								}
								catch (Exception e)
								{
									_logger($"ConnectionManager.OnDisconnect Exception: {rgws._displayId} {e.Message}", 0);
								}
								finally
								{
									_logger($"CleanupThread: Disposing {rgws._displayId}", 3);
									try
									{
										rgws.Shutdown();  // give C# scheduler just a moment to transition tasks status from "running" to "completed", because it always looks completed in the debugger with a breakpoint here.
										rgws.Dispose();
									}
									catch (Exception e)
									{
										_logger($"CleanupThread: Exception {rgws._displayId} {e.Message}", 0);
									}
								}
							}
							else  // it's too early to destroy this RGWS, it's still in its constructor.  Just put it back and wait until next time.  Order doesn't matter, they're just being destroyed.
							{
								_logger($"Spinlooping {rgws._displayId}", 3);
								_disconnected.Add(rgws);
							}
						}
						toDisconnect.Clear();
					}
				}
				catch (OperationCanceledException)  // if the token is cancelled, we pop to here
				{
				}
				catch (Exception e)
				{
					_logger($"WebSocketServer.CleanupThread - caught unexpected exception {e.Message}", 0);
				}
				finally
				{
					_logger("WebSocketServer.CleanupThread - exit", 2);
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
						_logger("WebSocketServer.HandleConnection - websocket detected.  Upgrading connection.", 2);
						using (CancellationTokenSource upgradeTimeout = new CancellationTokenSource(timeoutMS))
						{
							HttpListenerWebSocketContext webSocketContext = await Task.Run(async () => { return await httpContext.AcceptWebSocketAsync(null, _idleSeconds).ConfigureAwait(false); }, upgradeTimeout.Token);
							_logger("WebSocketServer.HandleConnection - websocket detected.  Upgraded.", 3);

							// Note, we hook our own OnDisconnect before proxying it on to the ConnectionManager.  Note, due to heavy congestion and C# scheduling, it's entirely possible that this is already a closed socket, and is immediately flagged for destruction.
							RGWebSocket rgws = new RGWebSocket(httpContext, _connectionManager.OnReceiveText, _connectionManager.OnReceiveBinary, OnDisconnection, _logger, httpContext.Request.RemoteEndPoint.ToString(), webSocketContext.WebSocket);
							_websockets.TryAdd(rgws._uniqueId, rgws);
							await _connectionManager.OnConnection(rgws).ConfigureAwait(false);
							_logger($"WebSocketServer.HandleConnection - websocket detected.  Upgrade completed. {rgws._displayId}", 3);
						}
					}
					catch (OperationCanceledException ex)  // timeout
					{
						_logger($"WebSocketServer.HandleConnection - websocket upgrade timeout {ex.Message}", 0);
						httpContext.Response.StatusCode = 500;
						httpContext.Response.Close();  // this breaks the connection, otherwise it may linger forever
					}
					catch (Exception ex)// anything else
					{
						_logger($"WebSocketServer.HandleConnection - websocket upgrade exception {ex.Message}", 0);
						httpContext.Response.StatusCode = 500;
						httpContext.Response.Close();  // this breaks the connection, otherwise it may linger forever
					}
				}
				else  // let the application specify what the HTTP response is, but we do the async write here to free up the app to do other things
				{
					try
					{
						_logger($"WebSocketServer.HandleConnection - normal http request {httpContext.Request.RawUrl}", 3);
						using (CancellationTokenSource responseTimeout = new CancellationTokenSource(timeoutMS))
						{
							// Remember to set httpContext.Response.StatusCode, httpContext.Response.ContentLength64, and httpContenxtResponse.OutputStream
							await _httpRequestCallback(httpContext).ConfigureAwait(false);
						}
					}
					catch (OperationCanceledException ex)  // timeout
					{
						_logger($"WebSocketServer.HandleConnection - websocket upgrade timeout {ex.Message}", 0);
						httpContext.Response.StatusCode = 500;
//						httpContext.Response.Abort();  // this breaks the connection, otherwise it may linger forever
					}
					catch (Exception ex) // anything else
					{
						_logger($"WebSocketServer.HandleConnection - http callback handler exception {ex.Message}", 0);
						httpContext.Response.StatusCode = 500;
//						httpContext.Response.Abort();  // this breaks the connection, otherwise it may linger forever
					}
					finally
					{
						httpContext.Response.Close();  // This frees all the memory associated with this connection.
					}
				}
			}

			// Add this websocket to the list of those we need to remove and unblock the cleanup thread
			private void OnDisconnection(RGWebSocket rgws)
			{
				_logger($"{rgws._displayId} OnDisconnection call.", 3);
				_disconnected.Add(rgws);  // finally, put it on the list of things to be disposed of
				_cleanupSocket.Set();  // Let the Cleanup process know there's something to do
			}
		}
	}
}