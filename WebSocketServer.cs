#nullable enable
﻿//-------------------
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
using DataCollection;
using Logging;
using Shared;

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
			private ILogging                  _logger;
			private IConnectionManager        _connectionManager;
			private RGWebSocketConfig         _config;

			// Distribution-oriented server metrics, fed automatically.  Read live any time, e.g. from a /metrics endpoint handler.
			public WebSocketServerMetrics     Metrics { get; } = new WebSocketServerMetrics();

			private Task                      _listenerUpdateTask = Task.CompletedTask;  // sleeps until one of the listeners finishes its work, then it creates a new one.  When _isRunning goes false, this exits.
			private HttpListener              _listener = new HttpListener();  // this is the listener but ALSO manages some internal structure for all WebSocket objects.  When you call .Close() on this, the listener stops but websockets do not abort.

			// Dead websockets are handed to the reaper task for final Shutdown(), because RGWebSocket's onDisconnection callback runs
			// ON the socket's own send task -- awaiting Shutdown() there would be waiting for yourself to finish.  The reaper does it from outside.
			private ChannelQueue<RGWebSocket> _reapQueue = new ChannelQueue<RGWebSocket>(singleReader: true, singleWriter: false);  // wakes the reaper on its own, no separate event needed
			private Task                      _reaperTask = Task.CompletedTask;
			private CancellationTokenSource?  _reaperCancel = null;  // also cancels the idle sweep -- they share a lifecycle
			private int                       _pendingReaps = 0;  // sockets queued for reaping or mid-reap; StopListening waits for this to hit zero

			// Idle sweep: every live socket is tracked here from Start() to disconnection.  A periodic task disconnects any socket that
			// hasn't RECEIVED data within config.IdleDisconnectSeconds, because transport idle timeouts can't be trusted behind an L7
			// proxy/Ingress (the proxy keeps its upstream connection warm, so the listener never sees the idleness).
			private ThreadSafeDictionary<RGWebSocket, RGWebSocket> _liveSockets = new ThreadSafeDictionary<RGWebSocket, RGWebSocket>();
			private Task                      _sweepTask = Task.CompletedTask;
			private List<RGWebSocket>         _sweepWorking = new List<RGWebSocket>();  // only ever touched by the single sweep task

			// httpRequest callback allows you to handle ordinary non-websocket HTTP requests however you want, even at the same url
			// dataCollection is nullable ON PURPOSE (not a default parameter): pass your IDataCollection derivative to have
			// connection/disconnect/message metrics pushed into it for prometheus scraping, or pass null explicitly if you don't.
			public WebSocketServer(int listenerTasks, int connectionMS, int idleSeconds, string prefixURL, OnHttpRequest httpRequest, IConnectionManager connectionManager, ILogging logger, RGWebSocketConfig config, IDataCollection? dataCollection)
			{
				if (logger==null)
					throw new ArgumentNullException(nameof(logger));
				if (connectionManager==null)
					throw new ArgumentNullException(nameof(connectionManager));
				if (config==null)
					throw new ArgumentNullException(nameof(config), "Pass RGWebSocketConfig.Default if you want the defaults.");
				if (dataCollection!=null)
					Metrics.AttachDataCollection(dataCollection);
				_listenerTasks       = listenerTasks;
				_connectionMS        = connectionMS;
				_prefixURL           = prefixURL;
				_httpRequestCallback = httpRequest;
				_logger              = logger;
				_connectionManager   = connectionManager;
				_idleSeconds         = idleSeconds;
				_config              = config;
			}

			public void Dispose()
			{
				StopListening().GetAwaiter().GetResult();  // block
				_logger.Log(EVerbosity.Info, $"WebSocketServer.Dispose");
			}

			public bool IsListening() { return _listener.IsListening; }

			public void StartListening()
			{
				if (IsListening())
					throw new InvalidOperationException("WebSocketServer.StartListening should not be called twice without StopListening");

				// Kick off the listener update task.  It makes sure there are always _maxCount listener threads available to accept incoming connections.
				try
				{
					_listener.Prefixes.Clear();
					_listener.Prefixes.Add(_prefixURL);
					_listener.TimeoutManager.IdleConnection = TimeSpan.FromSeconds(_idleSeconds);  // idle connections are cut after this long -- note, this doesn't affect websockets at all.
					_listener.Start();
					_reaperCancel = new CancellationTokenSource();
					_reaperTask = Task.Run(async () => await ReaperLoop(_reaperCancel.Token).ConfigureAwait(false));  // disposes dead sockets for the lifetime of the listener
					if (_config.IdleDisconnectSeconds>0)
						_sweepTask = Task.Run(async () => await IdleSweep(_reaperCancel.Token).ConfigureAwait(false));  // disconnects silent sockets, since transport idle timeouts can't be trusted
					_listenerUpdateTask = Task.Run(async () => await ListenerUpdate(_listenerTasks).ConfigureAwait(false));  // simply run the listener in a separate thread, so any websocketserver has its own cpu time dedicated
					_logger.Log(EVerbosity.Info, "WebSocketServer.Start");
				}
				catch (HttpListenerException e)
				{
					_logger.Log(EVerbosity.Error, $"WebSocketServer.StartListening exception (usually port conflict) {e.ErrorCode} {e}");
					throw;  // rethrow it, there's nothing we can do here
				}
			}

			// Blocks until the listener task is torn down.  Drains out the connection manager, then aborts any remaining connections.
			public async Task StopListening()
			{
				if (_listener.IsListening)
				{
					_logger.Log(EVerbosity.Info, "WebSocketServer.StopListening Listener.Close");
					_listener.Stop();  // cancel everything
					_logger.Log(EVerbosity.Info, "WebSocketServer.StopListening ConnectionManager.Shutdown");
					await _listenerUpdateTask.ConfigureAwait(false);  // wait until all the listener tasks have exited
					_listener.Close();  // dispose the listener
					await _connectionManager.Shutdown().ConfigureAwait(false);  // disconnect all existing RGWS connections

					// Every disconnect lands in the reaper queue; wait until the reaper has disposed them all, so shutdown is deterministic.
					long deadline = Environment.TickCount64 + 5000;
					while (Volatile.Read(ref _pendingReaps)>0 && Environment.TickCount64<deadline)
						await Task.Delay(20).ConfigureAwait(false);
					if (Volatile.Read(ref _pendingReaps)>0)
						_logger.Log(EVerbosity.Warning, $"WebSocketServer.StopListening: {_pendingReaps} sockets still unreaped after 5s");
					if (_reaperCancel!=null)
					{
						_reaperCancel.Cancel();
						await _reaperTask.ConfigureAwait(false);  // the reaper does one final sweep on the way out
						await _sweepTask.ConfigureAwait(false);
						_sweepTask = Task.CompletedTask;
						_reaperCancel.Dispose();
						_reaperCancel = null;
					}
					_logger.Log(EVerbosity.Info, "WebSocketServer.StopListening Complete");
				}
			}

			//-------------------
			// Runs for the lifetime of the listener.  Disposes dead sockets from outside their own task context, so RGWebSocket.Shutdown
			// never waits on itself no matter how the disconnect happened.
			private async Task ReaperLoop(CancellationToken token)
			{
				while (token.IsCancellationRequested==false)
				{
					try
					{
						await _reapQueue.WaitToReadAsync(token).ConfigureAwait(false);
					}
					catch (OperationCanceledException)  // not an error, flow control
					{
					}
					await ReapAll().ConfigureAwait(false);
				}
				await ReapAll().ConfigureAwait(false);  // final sweep, in case anything was queued while we were being told to exit
			}

			//-------------------
			// Wakes up periodically and disconnects any socket that hasn't received data within the configured window.  Receiving is
			// the only proof of liveness -- successful sends just fill kernel buffers -- so clients are expected to heartbeat more often
			// than IdleDisconnectSeconds.  Close() is graceful and its teardown is bounded even when the peer is truly gone.
			private async Task IdleSweep(CancellationToken token)
			{
				while (token.IsCancellationRequested==false)
				{
					try
					{
						await Task.Delay(_config.IdleSweepPeriodSeconds*1000, token).ConfigureAwait(false);
					}
					catch (OperationCanceledException)  // not an error, flow control
					{
						break;
					}
					// All the unit conversion happens HERE, once per sweep pass -- the per-frame stamp in RGWebSocket is just a raw clock read.
					long idleTimestampTicks = _config.IdleDisconnectSeconds * Stopwatch.Frequency;
					long now = Stopwatch.GetTimestamp();  // monotonic, immune to the NTP clock corrections that are common in containers/VMs
					_liveSockets.Foreach((rgws, unused) => { if (now - rgws.LastRecvTimestamp > idleTimestampTicks) _sweepWorking.Add(rgws); });  // collect under the read lock, act outside it
					for (int i=0; i<_sweepWorking.Count; i++)
					{
						_logger.Log(EVerbosity.Warning, $"WebSocketServer.IdleSweep disconnecting {_sweepWorking[i].DisplayId} (nothing received for over {_config.IdleDisconnectSeconds}s)");
						_sweepWorking[i].Close(EDisconnectReason.IdleTimeout);  // tagged so the disconnect metrics tell the true story
					}
					_sweepWorking.Clear();
				}
			}

			private List<RGWebSocket> _reapWorking = new List<RGWebSocket>();  // only ever touched by the single reaper task
			private async Task ReapAll()
			{
				_reapQueue.MoveTo(_reapWorking);  // bulk-drain under one lock, same pattern as the send queue
				for (int i=0; i<_reapWorking.Count; i++)
				{
					try
					{
						await _reapWorking[i].Shutdown().ConfigureAwait(false);
					}
					catch (Exception e)
					{
						_logger.Log(EVerbosity.Error, $"WebSocketServer.ReapAll {_reapWorking[i].DisplayId} {e}");
					}
					finally
					{
						Interlocked.Decrement(ref _pendingReaps);
					}
				}
				_reapWorking.Clear();
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
						if (_listener.IsListening)
						{
							Task<HttpListenerContext> t = _listener.GetContextAsync();
							listenerTasks.Add(t);
							_logger.Log(EVerbosity.Extreme, "WebSocketServer.ListenerUpdate - adding listener");
						}
					}

					while (_listener.IsListening)  // loop forever until canceled
					{
						using (Task t = await Task.WhenAny(listenerTasks).ConfigureAwait(false))
						{
							listenerTasks.Remove(t);

							// Note, listenerTasks is used for BOTH listening and for tasks that run for websockets and regular HTTP requests
							// Which is why sometimes it is a Task<HttpListenerContext> and sometimes is just a regular Task that needs to be disposed when it completes.
							// That's also why we don't want to add new listener tasks except when the task /was/ a listener that just completed.  Otherwise the number of listener tasks will grow.
							if (t is Task<HttpListenerContext> connectTask)
							{
								_logger.Log(EVerbosity.Extreme, $"WebSocketServer.ListenerUpdate - listener handled {connectTask.Status}");

								// replace the listener task that just finished if the socket listener is still running
								if (_listener.IsListening)
								{
									try
									{
										Task newListener = _listener.GetContextAsync();
										listenerTasks.Add(newListener);
									}
									catch (ObjectDisposedException)
									{
										// Listener closed between IsListening check and GetContextAsync; ignore and allow loop to exit.
									}
								}

								// If the connection was valid, go ahead and handle the request
								if (connectTask.IsCompletedSuccessfully)
								{
									// Actually handle the connection
									HttpListenerContext connectContext = await connectTask.ConfigureAwait(false);  // this task is already complete, so it does not block
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
				catch (ObjectDisposedException)
				{
					// Listener disposed during shutdown; treat as normal exit path.
				}
				catch (Exception e)
				{
					_logger.Log(EVerbosity.Error, $"WebSocketServer.ListenerUpdate - caught unexpected exception {e}");
				}
				finally
				{
					_logger.Log(EVerbosity.Extreme, "WebSocketServer.ListenerUpdate - disposing listener tasks");

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
					_logger.Log(EVerbosity.Extreme, "WebSocketServer.ListenerUpdate - listener tasks dead");
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
						_logger.Log(EVerbosity.Info, "WebSocketServer.HandleConnection - websocket detected.  Upgrading connection.");
						using (CancellationTokenSource upgradeTimeout = new CancellationTokenSource(timeoutMS))
						{
							// The TimeSpan is supposed to be the websocket keepalives.  But does not appear to affect anything. I would disable keepalives if I could.
							HttpListenerWebSocketContext webSocketContext = await httpContext.AcceptWebSocketAsync(null, TimeSpan.FromSeconds(1)).WaitAsync(upgradeTimeout.Token).ConfigureAwait(false);
							_logger.Log(EVerbosity.Debug, "WebSocketServer.HandleConnection - websocket detected.  Upgraded.");

							// Note, we hook our own OnReceive/OnDisconnect before proxying it on to the ConnectionManager.  The constructor is inert:
							// the connection manager gets to register the socket FIRST, and only then does Start() spin up the pumps, so no
							// callback can ever race the registration.
							RGWebSocket rgws = new RGWebSocket(httpContext, OnReceive, OnDisconnection, _logger, httpContext.Request.RemoteEndPoint.ToString(), webSocketContext.WebSocket, _config);
							bool recordedConnect = false;
							try
							{
								await _connectionManager.OnConnection(rgws, httpContext).ConfigureAwait(false);
								_liveSockets.Add(rgws, rgws);  // tracked for the idle sweep; removed in OnDisconnection
								Metrics.RecordConnect();
								recordedConnect = true;
								rgws.Start();
								_logger.Log(EVerbosity.Debug, $"WebSocketServer.HandleConnection - websocket detected.  Upgrade completed. {rgws.DisplayId}");
							}
							catch
							{
								_liveSockets.Remove(rgws);
								await rgws.Shutdown().ConfigureAwait(false);  // the manager rejected it (threw), so dispose the never-started socket cleanly
								if (recordedConnect)
									Metrics.RecordDisconnect(rgws);  // keep connect/disconnect counts balanced
								throw;
							}
						}
					}
					catch (OperationCanceledException ex)  // timeout
					{
						_logger.Log(EVerbosity.Error, $"WebSocketServer.HandleConnection - websocket upgrade timeout {ex}");
						try
						{
							httpContext.Response.StatusCode = 500;
							httpContext.Response.Close();  // this breaks the connection, otherwise it may linger forever
						}
						catch (Exception)  // HttpListenerResponse throws if the upgrade already touched it; the connection is dead either way
						{
						}
					}
					catch (Exception ex)// anything else
					{
						_logger.Log(EVerbosity.Error, $"WebSocketServer.HandleConnection - websocket upgrade exception {ex}");
						try
						{
							httpContext.Response.StatusCode = 500;
							httpContext.Response.Close();  // this breaks the connection, otherwise it may linger forever
						}
						catch (Exception)  // HttpListenerResponse throws if the upgrade already touched it; the connection is dead either way
						{
						}
					}
				}
				else  // let the application specify what the HTTP response is, but we do the async write here to free up the app to do other things
				{
					try
					{
						_logger.Log(EVerbosity.Debug, $"WebSocketServer.HandleConnection - normal http request {httpContext.Request.RawUrl}");
						using (CancellationTokenSource responseTimeout = new CancellationTokenSource(timeoutMS))
						{
							// Remember to set httpContext.Response.StatusCode, httpContext.Response.ContentLength64, and httpContenxtResponse.OutputStream
							await _httpRequestCallback(httpContext).WaitAsync(responseTimeout.Token).ConfigureAwait(false);  // a handler that overruns the timeout gets abandoned and the connection closed
						}
					}
					catch (OperationCanceledException ex)  // timeout
					{
						_logger.Log(EVerbosity.Error, $"WebSocketServer.HandleConnection - http callback cancelled {ex}");
					}
					catch (Exception ex) // anything else
					{
						_logger.Log(EVerbosity.Error, $"WebSocketServer.HandleConnection - http callback handler exception {ex}");
					}
					finally
					{
						httpContext.Response.Close();  // This frees all the memory associated with this connection.
					}
				}
			}

			// The core delivers text messages as UTF8 bytes in a pooled buffer; decode here at the edge, since IConnectionManager wants strings for text.
			private Task OnReceive(RGWebSocket rgws, PooledArray msg, bool isText)
			{
				Metrics.RecordInboundMessage(msg.Length);
				if (isText)
					return _connectionManager.OnReceiveText(rgws, System.Text.Encoding.UTF8.GetString(msg.data, 0, msg.Length));
				return _connectionManager.OnReceiveBinary(rgws, msg);
			}

			// Add this websocket to the list of those we need to remove and unblock the cleanup thread
			private async Task OnDisconnection(RGWebSocket rgws)
			{
				_logger.Log(EVerbosity.Debug, $"{rgws.DisplayId} OnDisconnection call.");
				try
				{
					await _connectionManager.OnDisconnect(rgws).ConfigureAwait(false);  // let the connection manager know it's disconnected now
				}
				catch (Exception e)
				{
					_logger.Log(EVerbosity.Error, $"WebSocketServer.OnDisconnection Exception: {rgws.DisplayId} {e.Message}");
				}
				finally
				{
					// Hand the socket to the reaper for final disposal.  We are running ON this socket's send task right now, so awaiting
					// rgws.Shutdown() here would deadlock waiting for ourselves.  The reaper shuts it down from outside instead.
					_logger.Log(EVerbosity.Debug, $"WebSocketServer.OnDisconnection queued for reaping {rgws.DisplayId}");
					_liveSockets.Remove(rgws);  // no longer a candidate for the idle sweep
					Metrics.RecordDisconnect(rgws);  // fold this socket's lifetime stats into the distributions, tagged by cause
					Interlocked.Increment(ref _pendingReaps);
					_reapQueue.Add(rgws);  // the reaper's WaitToReadAsync wakes on its own
				}
			}
		}
	}
}