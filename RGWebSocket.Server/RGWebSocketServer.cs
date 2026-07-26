#nullable enable
//-------------------
// Reachable Games
// Copyright 2023
//-------------------

using DataCollection;
using Logging;
using Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		// This class handles setting up the listener tasks, handling HTTP(S)/WS(S) connections, deciding if they are http or websocket and upgrading them,
		// and making the appropriate callbacks to upstream code, managing the shutdown process, etc.  Note, this class owns the actual RGWebSocket connections.
		// Anytime a HttpListener is stopped, it aborts all the websocket connections.
		public class RGWebSocketServer : IDisposable
		{
			// token fires when the request's deadline (connectionMS) passes; the callback should observe it and stop working.
			public delegate Task OnHttpRequest(HttpListenerContext httpContext, CancellationToken token);

			// What is safe to put in a log line about a request.  NEVER log the raw URL on these paths: the query string
			// routinely carries session tokens and API keys (and websocket clients have no other place to put them, since
			// browsers cannot set headers on an upgrade), so logging it copies credentials into logs that get shipped,
			// indexed, and shared.  It is also attacker-sized -- a 16KB URL times a flood of refused requests is a log
			// amplification attack.  Path only, truncated.
			static internal string SafeUrl(HttpListenerContext ctx)
			{
				string path;
				try
				{ path = ctx.Request.Url?.AbsolutePath ?? "/"; }
				catch (Exception) { return "<unavailable>"; }
				return path.Length <= 256 ? path : path.Substring(0, 256) + "...";
			}

			// Pre-upgrade gate for websockets: runs BEFORE the handshake is accepted, so a denial is a plain HTTP
			// status (401/403/...) and never costs a socket.  This is where Origin validation and connection auth
			// belong -- browsers place no restriction on which sites may open a websocket to you (cross-site websocket
			// hijacking), so the server must.  The full request (headers, cookies, query string) is available on the
			// context.  Return null to admit the upgrade, or a ready-to-send (status, contentType, body) denial.
			// Throwing fails CLOSED (500, nobody admitted).  OnConnection still runs after the accept and can also
			// reject by throwing, but that is post-handshake -- the peer sees a 101 then an abort, not a status.
			// The token fires at the connection deadline.  OBSERVE IT: an authorizer typically calls a token service or
			// database, and abandoning the wait does not abandon that call -- without the token, a hung dependency keeps
			// piling up live work per refused connection while the server has already answered.
			public delegate Task<(int, string, byte[])?> UpgradeAuthorizer(HttpListenerContext httpContext, CancellationToken token);

			private int                 _listenerTasks;
			private int                 _connectionMS;
			private int                 _idleSeconds;
			private string              _prefixURL;
			private OnHttpRequest       _httpRequestCallback;
			private ILogging            _logger;
			private RGConnectionManager _connectionManager;
			private UpgradeAuthorizer?  _upgradeAuthorizer; // nullable ON PURPOSE: null = every upgrade admitted (public server), non-null runs before every handshake

			// Distribution-oriented server metrics, fed automatically.  Read live any time, e.g. from a /metrics endpoint handler.
			public WebSocketServerMetrics Metrics { get; } = new WebSocketServerMetrics();

			private          Task         _listenerUpdateTask = Task.CompletedTask; // sleeps until one of the listeners finishes its work, then it creates a new one.  When _isRunning goes false, this exits.
			private          HttpListener _listener           = new HttpListener(); // this is the listener but ALSO manages some internal structure for all WebSocket objects.  When you call .Close() on this, the listener stops but websockets do not abort.
			private volatile bool         _draining           = false;              // set by StopListening: refuse new websocket upgrades while existing connections drain, so none is left mid-I/O when the listener stops

			// Dead websockets are handed to the reaper task for final Shutdown(), because RGWebSocket's onDisconnection callback runs
			// ON the socket's own send task -- awaiting Shutdown() there would be waiting for yourself to finish.  The reaper does it from outside.
			private ChannelQueue<RGWebSocket> _reapQueue    = new ChannelQueue<RGWebSocket>(singleReader: true, singleWriter: false); // wakes the reaper on its own, no separate event needed
			private Task                      _reaperTask   = Task.CompletedTask;
			private CancellationTokenSource?  _reaperCancel = null; // also cancels the idle sweep -- they share a lifecycle
			private int                       _pendingReaps = 0;    // sockets queued for reaping or mid-reap; StopListening waits for this to hit zero

			// Idle sweep: every live socket is tracked here from Start() to disconnection.  A periodic task disconnects any socket that
			// hasn't RECEIVED data within config.IdleDisconnectSeconds, because transport idle timeouts can't be trusted behind an L7
			// proxy/Ingress (the proxy keeps its upstream connection warm, so the listener never sees the idleness).
			private ThreadSafeHashSet<RGWebSocket> _liveSockets = new ThreadSafeHashSet<RGWebSocket>();
			private long              _nextRefusalLogMs = 0; // rate-limits the MaxConcurrentWebSockets refusal warning
			private long              _refusalsSinceLog = 0;
			private long              _nextDenialLogMs  = 0; // rate-limits the upgrade-authorizer denial log; probes arrive in floods
			private long              _denialsSinceLog  = 0;
			private int               _pendingUpgrades  = 0; // handshakes in flight: accepted by the listener but not yet in _liveSockets.  StopListening MUST wait on this (see the note there).
			private Task              _sweepTask        = Task.CompletedTask;
			private List<RGWebSocket> _sweepWorking     = new List<RGWebSocket>(); // only ever touched by the single sweep task

			// httpRequest callback allows you to handle ordinary non-websocket HTTP requests however you want, even at the same url
			// dataCollection is nullable ON PURPOSE (not a default parameter): pass your IDataCollection derivative to have
			// connection/disconnect/message metrics pushed into it for prometheus scraping, or pass null explicitly if you don't.
			// upgradeAuthorizer is nullable the same way: pass your Origin/auth gate to run before every websocket handshake, or null explicitly for a public server.
			public RGWebSocketServer(int listenerTasks, int connectionMS, int idleSeconds, string prefixURL, OnHttpRequest httpRequest, RGConnectionManager connectionManager, ILogging logger, IDataCollection? dataCollection, UpgradeAuthorizer? upgradeAuthorizer)
			{
				if (logger == null)
					throw new ArgumentNullException(nameof(logger));
				if (connectionManager == null)
					throw new ArgumentNullException(nameof(connectionManager));
				RGWebSocketConfig.MarkInUse(); // from here on, Configure() throws -- the sweep and sockets read the config unsynchronized
				if (dataCollection != null)
					Metrics.AttachDataCollection(dataCollection);
				_listenerTasks       = listenerTasks;
				_connectionMS        = connectionMS;
				_prefixURL           = prefixURL;
				_httpRequestCallback = httpRequest;
				_logger              = logger;
				_connectionManager   = connectionManager;
				_idleSeconds         = idleSeconds;
				_upgradeAuthorizer   = upgradeAuthorizer;
			}

			public void Dispose()
			{
				StopListening().GetAwaiter().GetResult(); // block
				_logger.Log(EVerbosity.Info, $"WebSocketServer.Dispose");
			}

			public bool IsListening() { try { return _listener.IsListening; } catch (ObjectDisposedException) { return false; } } // a closed listener is not listening, it is not an error to ask

			public void StartListening()
			{
				if (IsListening())
					throw new InvalidOperationException("WebSocketServer.StartListening should not be called twice without StopListening");

				// Kick off the listener update task.  It makes sure there are always _maxCount listener threads available to accept incoming connections.
				try
				{
					_draining = false; // fresh start (supports Stop/Start reuse)
										// Stop/Start reuse needs BOTH of these rebuilt, because StopListening ends them permanently: it
										// Close()s the listener (disposed -- Prefixes/Start throw on it afterward) and Complete()s the reap
										// queue (closed to writers forever, so every socket of the second run would be refused reaping and
										// never disposed).  Neither can be reopened, so a reused server gets fresh ones.
					_listener  = new HttpListener();
					_reapQueue = new ChannelQueue<RGWebSocket>(singleReader: true, singleWriter: false);
					_listener.Prefixes.Add(_prefixURL);
					_listener.TimeoutManager.IdleConnection = TimeSpan.FromSeconds(_idleSeconds); // idle connections are cut after this long -- note, this doesn't affect websockets at all.
					_listener.Start();
					_reaperCancel = new CancellationTokenSource();
					_reaperTask   = Task.Run(async () => await ReaperLoop(_reaperCancel.Token).ConfigureAwait(false)); // disposes dead sockets for the lifetime of the listener
					if (RGWebSocketConfig.IdleDisconnectSeconds > 0)
						_sweepTask = Task.Run(async () => await IdleSweep(_reaperCancel.Token).ConfigureAwait(false)); // disconnects silent sockets, since transport idle timeouts can't be trusted
					_listenerUpdateTask = Task.Run(async () => await ListenerUpdate(_listenerTasks).ConfigureAwait(false)); // simply run the listener in a separate thread, so any websocketserver has its own cpu time dedicated
					_logger.Log(EVerbosity.Info, "WebSocketServer.Start");
				}
				catch (HttpListenerException e)
				{
					_logger.Log(EVerbosity.Error, $"WebSocketServer.StartListening exception (usually port conflict) {e.ErrorCode} {e}");
					throw; // rethrow it, there's nothing we can do here
				}
			}

			// Blocks until the listener task is torn down.  Drains out the connection manager, then stops the listener.
			//
			// ORDERING IS LOAD-BEARING.  Every accepted HttpListenerWebSocket allocates its send/receive overlappeds from
			// the HttpListener's ThreadPoolBoundHandle -- a SINGLE handle shared by all connections on this listener.  Both
			// _listener.Stop() AND _listener.Close() dispose that handle, so ANY websocket still doing I/O when the listener
			// stops touches a freed handle.  When that access lands on the send task it is a caught ObjectDisposedException
			// on ThreadPoolBoundHandle; when it lands on the receive IOCP completion (ThreadPoolBoundHandle.OnNativeIOCompleted)
			// it is an UNHANDLED 'overlapped has already been freed' that crashes the whole process.  Same root cause, two
			// landing spots.  Therefore we must fully DRAIN and REAP every connection while the listener (and its handle) is
			// still completely live, and only THEN stop it.  New upgrades are refused during the drain (_draining) so the
			// set we wait on cannot grow.  A surgical repro that streams traffic then tears the listener down hit the
			// disposed-handle exception on ~12% of teardowns with the old ordering; drain-first takes it to zero.
			public async Task StopListening()
			{
				if (_listener.IsListening && _draining == false)
				{
					_draining = true; // HandleConnection now refuses new websocket upgrades; existing ones are unaffected

					// 1) Close and fully reap every LIVE connection while the listener's shared handle is still valid, so
					//    graceful close frames and any in-flight receives complete against a live handle, not a freed one.
					_logger.Log(EVerbosity.Info, "WebSocketServer.StopListening ConnectionManager.Shutdown");
					await _connectionManager.Shutdown().ConfigureAwait(false); // app cleanup + closes the sockets IT tracks
																				// Belt and suspenders: close any accepted socket the app did NOT track (e.g. connected but not yet in a
																				// lobby).  EVERY accepted socket must be reaped before the handle is disposed, or its posted receive is
																				// the one that fires OnNativeIOCompleted on a freed overlapped.  Close() is idempotent per socket.
					_liveSockets.Foreach((rgws) => rgws.Close(EDisconnectReason.LocalShutdown));

					// Wait for every socket to finish DISCONNECTING and be reaped.  All THREE conditions are load-bearing:
					// _pendingReaps only increments when a socket REACHES disconnection, so right after Close() it can
					// still be zero while every socket is mid-teardown with receives posted -- waiting on it alone fell
					// straight through to disposing the shared handle (the exact crash described above).  A socket is
					// always visible in _liveSockets OR _pendingReaps (OnDisconnection increments the reap count BEFORE
					// leaving the live set), so those two have no slip window between them.
					// _pendingUpgrades covers the window BEFORE either exists: a request that passed the _draining check
					// microseconds before we set it is still inside AcceptWebSocketAsync, invisible to both other counters,
					// and it lands in _liveSockets and posts a receive AFTER this wait would otherwise have finished --
					// disposing the handle under it is the native crash this whole ordering exists to prevent.  It is
					// incremented BEFORE the draining re-check, so a handshake is never invisible to us.
					long deadline = Environment.TickCount64 + 30000;
					while ((_liveSockets.Count > 0 || Volatile.Read(ref _pendingReaps) > 0 || Volatile.Read(ref _pendingUpgrades) > 0) && Environment.TickCount64 < deadline)
						await Task.Delay(20).ConfigureAwait(false);

					if (_liveSockets.Count > 0 || Volatile.Read(ref _pendingReaps) > 0 || Volatile.Read(ref _pendingUpgrades) > 0)
					{
						// The invariant cannot be met: disposing the listener now would free the ThreadPoolBoundHandle
						// under live receive I/O -- a NATIVE crash, not an exception.  Leak the listener instead (the
						// process is exiting or the port stays claimed until it does); a leak is recoverable, the crash
						// is not.  This state means a socket's teardown is wedged, which is its own bug -- say so loudly.
						_logger.Log(EVerbosity.Error, $"WebSocketServer.StopListening: {_liveSockets.Count} live / {_pendingReaps} unreaped / {_pendingUpgrades} upgrading sockets after 30s; LEAKING the listener instead of disposing its handle under live I/O.");
					}
					else
					{
						// 2) No websocket holds an overlapped against the handle anymore -- now it is safe to stop and dispose the listener.
						_logger.Log(EVerbosity.Info, "WebSocketServer.StopListening Listener.Stop");
						_listener.Stop(); // aborts only the pending GetContextAsync accepts, which have no websockets behind them
						await _listenerUpdateTask.ConfigureAwait(false); // wait until all the listener accept tasks have exited
						_listener.Close(); // dispose the listener and its now-unused ThreadPoolBoundHandle
					}

					// 3) Stop the reaper and idle sweep (they shared a lifecycle with the listener).
					if (_reaperCancel != null)
					{
						_reaperCancel.Cancel();
						await _reaperTask.ConfigureAwait(false); // the reaper does one final sweep on the way out
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
				while (token.IsCancellationRequested == false)
				{
					try
					{
						await _reapQueue.WaitToReadAsync(token).ConfigureAwait(false);
					}
					catch (OperationCanceledException) // not an error, flow control
					{
					}
					await ReapAll().ConfigureAwait(false);
				}
				// Close the queue BEFORE the final sweep, same reasoning as the send queue: once this task exits, an Add
				// would silently park a socket in a queue with no consumer -- never Shutdown(), never disposed, and its
				// _pendingReaps increment never undone, which would wedge any later StopListening for a full 30s.
				// Completing first means a late OnDisconnection is TOLD nobody is listening and can account for itself.
				_reapQueue.Complete();
				await ReapAll().ConfigureAwait(false); // final sweep, in case anything was queued while we were being told to exit
			}

			//-------------------
			// Wakes up periodically and disconnects any socket that hasn't received data within the configured window.  Receiving is
			// the only proof of liveness -- successful sends just fill kernel buffers -- so clients are expected to heartbeat more often
			// than IdleDisconnectSeconds.  Close() is graceful and its teardown is bounded even when the peer is truly gone.
			private async Task IdleSweep(CancellationToken token)
			{
				while (token.IsCancellationRequested == false)
				{
					try
					{
						await Task.Delay(RGWebSocketConfig.IdleSweepPeriodSeconds * 1000, token).ConfigureAwait(false);
					}
					catch (OperationCanceledException) // not an error, flow control
					{
						break;
					}
					// All the unit conversion happens HERE, once per sweep pass -- the per-frame stamp in RGWebSocket is just a raw clock read.
					long idleTimestampTicks = RGWebSocketConfig.IdleDisconnectSeconds * Stopwatch.Frequency;
					long now                = Stopwatch.GetTimestamp(); // monotonic, immune to the NTP clock corrections that are common in containers/VMs
					_liveSockets.Foreach((rgws) => { if (now - rgws.LastRecvTimestamp > idleTimestampTicks) _sweepWorking.Add(rgws); }); // collect under the read lock, act outside it
					for (int i = 0; i < _sweepWorking.Count; i++)
					{
						_logger.Log(EVerbosity.Warning, $"WebSocketServer.IdleSweep disconnecting {_sweepWorking[i].DisplayId} (nothing received for over {RGWebSocketConfig.IdleDisconnectSeconds}s)");
						_sweepWorking[i].Close(EDisconnectReason.IdleTimeout); // tagged so the disconnect metrics tell the true story
					}
					_sweepWorking.Clear();
				}
			}

			private       List<RGWebSocket> _reapWorking = new List<RGWebSocket>(); // only ever touched by the single reaper task
			private async Task              ReapAll()
			{
				_reapQueue.MoveTo(_reapWorking); // bulk-drain under one lock, same pattern as the send queue
				for (int i = 0; i < _reapWorking.Count; i++)
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
					for (int i = 0; i < numListenerTasks; i++)
					{
						if (_listener.IsListening)
						{
							Task<HttpListenerContext> t = _listener.GetContextAsync();
							listenerTasks.Add(t);
							_logger.Log(EVerbosity.Extreme, "WebSocketServer.ListenerUpdate - adding listener");
						}
					}

					while (_listener.IsListening) // loop forever until canceled
					{
						// Shutdown race: Stop() faults the pending GetContextAsync tasks BEFORE IsListening reads false, so the
						// set can drain to empty while the loop condition still passes -- and WhenAny(empty) throws.  Drained
						// means shutdown is already underway; just exit.
						if (listenerTasks.Count == 0)
							break;
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
									HttpListenerContext connectContext = await connectTask.ConfigureAwait(false); // this task is already complete, so it does not block
									Task                connectionTask = HandleConnection(connectContext);
									listenerTasks.Add(connectionTask);
								}
							}
						}
					}
				}
				catch (OperationCanceledException) // if the token is cancelled, we pop to here
				{
				}
				catch (Exception e) when (TransportTeardown.IsExpected(e))
				{
					// Listener disposed/aborted during shutdown (Stop() faults everything before IsListening reads false);
					// treat as the normal exit path (contract: TransportTeardown.cs).
					_logger.Log(EVerbosity.Debug, $"WebSocketServer.ListenerUpdate - listener torn down {e.GetType().Name}: {e.Message}");
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
						catch // eat any exceptions--we don't really care
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
				// Shutdown in progress: refuse new websocket upgrades so the drain in StopListening cannot race a socket
				// that starts its receive pump (and posts an overlapped against the handle) after the drain has passed.
				if (_draining && httpContext.Request.IsWebSocketRequest)
				{
					try
					{ httpContext.Response.StatusCode = 503; httpContext.Response.Close(); }
					catch (Exception) { }
					return;
				}

				// RESERVE the slot before testing the cap, so this handshake is visible to every other one racing it.
				// The reservation is released by the finally at the bottom of the websocket branch (every path from here
				// on either reaches it or returns through the refusal/denial paths below, which release it themselves).
				if (httpContext.Request.IsWebSocketRequest)
					Interlocked.Increment(ref _pendingUpgrades);

				// Connection-count circuit breaker (RGWebSocketConfig.MaxConcurrentWebSockets): every other breaker is
				// per-connection, so without this one an attacker simply opens connections until the process dies of
				// memory or handle exhaustion.  Refusals are counted in the metrics and the log line is rate-limited --
				// during an actual flood, one Warning per interval tells the story without the log becoming the victim.
				// The test counts LIVE PLUS IN-FLIGHT (including our own reservation): a socket does not reach _liveSockets
				// until its handshake finishes, so testing live-only let a simultaneous burst of N handshakes all read the
				// same under-cap number and sail through together -- the cap held on average and not at all under a burst,
				// which is precisely when it matters.
				if (httpContext.Request.IsWebSocketRequest && RGWebSocketConfig.MaxConcurrentWebSockets > 0 && _liveSockets.Count + Volatile.Read(ref _pendingUpgrades) > RGWebSocketConfig.MaxConcurrentWebSockets)
				{
					Interlocked.Decrement(ref _pendingUpgrades); // release the reservation we just took; we are not proceeding
					Metrics.RecordRefusedUpgrade();
					long refused = Interlocked.Increment(ref _refusalsSinceLog);
					long nowTick = Environment.TickCount64;
					long nextLog = Interlocked.Read(ref _nextRefusalLogMs);
					if (nowTick >= nextLog && Interlocked.CompareExchange(ref _nextRefusalLogMs, nowTick + 10_000, nextLog) == nextLog)
					{
						Interlocked.Add(ref _refusalsSinceLog, -refused);
						_logger.Log(EVerbosity.Warning, $"WebSocketServer.HandleConnection - refusing websocket upgrades: {_liveSockets.Count} live + {Volatile.Read(ref _pendingUpgrades)} upgrading >= MaxConcurrentWebSockets={RGWebSocketConfig.MaxConcurrentWebSockets} ({refused} refused since last note)");
					}
					try
					{ httpContext.Response.StatusCode = 503; httpContext.Response.Close(); }
					catch (Exception) { }
					return;
				}

				// Pre-upgrade authorization: Origin/auth checks run BEFORE the handshake is accepted, so a denial is a
				// plain HTTP status and never costs a socket.  (OnConnection can still reject after the accept, but by
				// then the 101 has already gone out.)  An authorizer that throws -- or that overruns the connection
				// deadline, since it typically calls out to a token service or database that can hang -- fails CLOSED.
				if (httpContext.Request.IsWebSocketRequest && _upgradeAuthorizer != null)
				{
					(int, string, byte[])? deny = null;
					try
					{
						// The token goes INTO the authorizer so its underlying work can actually stop; WaitAsync only guards
						// against an authorizer that ignores it.
						using (CancellationTokenSource authTimeout = new CancellationTokenSource(Debugger.IsAttached ? -1 : _connectionMS))
							deny = await _upgradeAuthorizer(httpContext, authTimeout.Token).WaitAsync(authTimeout.Token).ConfigureAwait(false);
					}
					catch (OperationCanceledException) // a hung authorizer must not pin the connection open indefinitely
					{
						_logger.Log(EVerbosity.Error, $"WebSocketServer.HandleConnection - upgrade authorizer overran the {_connectionMS}ms deadline; denying.  {SafeUrl(httpContext)}");
						deny = (503, "text/plain", System.Text.Encoding.UTF8.GetBytes("503 Service Unavailable"));
					}
					catch (Exception e)
					{
						_logger.Log(EVerbosity.Error, $"WebSocketServer.HandleConnection - exception in upgrade authorizer {SafeUrl(httpContext)} {e}");
						deny = (500, "text/plain", System.Text.Encoding.UTF8.GetBytes("500 Internal Server Error"));
					}
					if (deny != null)
					{
						Interlocked.Decrement(ref _pendingUpgrades); // release the reservation; this handshake is not happening
						(int denyStatus, string denyType, byte[] denyBody) = deny.Value;
						// Throttled: denial is the EXPECTED response to a hostile probe, and probes arrive in floods --
						// an unthrottled line per denial makes the log the next victim.  One line per interval, with the
						// suppressed count, plus the always-accurate Metrics.DeniedUpgrades counter.
						Metrics.RecordDeniedUpgrade();
						long denied  = Interlocked.Increment(ref _denialsSinceLog);
						long nowTick = Environment.TickCount64;
						long nextLog = Interlocked.Read(ref _nextDenialLogMs);
						if (nowTick >= nextLog && Interlocked.CompareExchange(ref _nextDenialLogMs, nowTick + 10_000, nextLog) == nextLog)
						{
							Interlocked.Add(ref _denialsSinceLog, -denied);
							_logger.Log(EVerbosity.Info, $"WebSocketServer.HandleConnection - websocket upgrade denied ({denyStatus}) {SafeUrl(httpContext)} from {httpContext.Request.RemoteEndPoint} ({denied} denied since last note)");
						}
						try
						{
							httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
							httpContext.Response.StatusCode                        = denyStatus;
							httpContext.Response.ContentType                       = denyType;
							httpContext.Response.ContentLength64                   = denyBody.Length;
							await httpContext.Response.OutputStream.WriteAsync(denyBody, 0, denyBody.Length).ConfigureAwait(false);
							httpContext.Response.Close();
						}
						catch (Exception) // the prober hung up mid-denial; nothing to salvage
						{
							try
							{ httpContext.Response.Abort(); }
							catch (Exception) { }
						}
						return;
					}
				}

				// Allow debugging to actually happen, where you have unlimited time to check things without breaking a connection.  -1 means don't cancel over time.
				int timeoutMS = Debugger.IsAttached ? -1 : _connectionMS;
				if (httpContext.Request.IsWebSocketRequest)
				{
					// The reservation taken before the cap check above is still held here, and it is what makes the
					// _draining re-check below safe: between the fast-path check at the top of this method and this point,
					// StopListening can flip _draining and run its whole drain to completion, and this upgrade would then
					// add a live socket (posting a receive against the shared handle) after the drain believed it was
					// finished.  Because we reserved first and re-check second, StopListening either sees our reservation
					// and waits for us, or we see _draining and refuse -- never neither.
					try
					{
						if (_draining)
						{
							try
							{ httpContext.Response.StatusCode = 503; httpContext.Response.Close(); }
							catch (Exception) { }
							return;
						}

						// Kick off an async task to upgrade the web socket and do send/recv messaging, but fail if it takes more than a second to finish.
						try
						{
							_logger.Log(EVerbosity.Info, "WebSocketServer.HandleConnection - websocket detected.  Upgrading connection.");
							using (CancellationTokenSource upgradeTimeout = new CancellationTokenSource(timeoutMS))
							{
								// The TimeSpan is the websocket protocol keepalive interval.  It was hardcoded at ONE SECOND, which
								// is a ping per socket per second -- 40k packets/sec at a 40k-socket cap -- to observe liveness it
								// cannot actually observe: through an ingress/proxy this only covers the server<->proxy hop, and
								// .NET answers pings internally so they never refresh the idle sweep either.  See the long note on
								// RGWebSocketConfig.WebSocketKeepAliveSeconds; the idle sweep is the real liveness mechanism.
								HttpListenerWebSocketContext webSocketContext = await httpContext.AcceptWebSocketAsync(null, RGWebSocketConfig.WebSocketKeepAliveInterval).WaitAsync(upgradeTimeout.Token).ConfigureAwait(false);
								_logger.Log(EVerbosity.Debug, "WebSocketServer.HandleConnection - websocket detected.  Upgraded.");

								// Note, we hook our own OnReceive/OnDisconnect before proxying it on to the ConnectionManager.  The constructor is inert:
								// the connection manager gets to register the socket FIRST, and only then does Start() spin up the pumps, so no
								// callback can ever race the registration.
								RGWebSocket rgws            = new RGWebSocket(httpContext, OnReceive, OnDisconnection, _logger, httpContext.Request.RemoteEndPoint.ToString(), webSocketContext.WebSocket);
								bool        recordedConnect = false;
								try
								{
									await _connectionManager.OnConnection(rgws, httpContext).ConfigureAwait(false);
									_liveSockets.Add(rgws); // tracked for the idle sweep; removed in OnDisconnection
									Metrics.RecordConnect();
									recordedConnect = true;
									rgws.Start();
									// Shutdown may have begun while we were upgrading.  StopListening's Close() sweep has already
									// run past us, so nothing else will ever close this socket -- do it here, or the drain waits
									// out its full 30s on a socket nobody asked to stop and then leaks the listener.
									if (_draining)
										rgws.Close(EDisconnectReason.LocalShutdown);
									_logger.Log(EVerbosity.Debug, $"WebSocketServer.HandleConnection - websocket detected.  Upgrade completed. {rgws.DisplayId}");
								}
								catch
								{
									_liveSockets.Remove(rgws);
									await rgws.Shutdown().ConfigureAwait(false); // the manager rejected it (threw), so dispose the never-started socket cleanly
									if (recordedConnect)
										Metrics.RecordDisconnect(rgws); // keep connect/disconnect counts balanced
									throw;
								}
							}
						}
						catch (OperationCanceledException) // timeout -- the client stalled the upgrade handshake and is almost
						{ // certainly gone (chaos/malice/flaky network).  Client-caused, not actionable.
							_logger.Log(EVerbosity.Debug, $"WebSocketServer.HandleConnection - websocket upgrade abandoned after {timeoutMS}ms (client stalled) {SafeUrl(httpContext)}");
							try
							{
								httpContext.Response.StatusCode = 500;
								httpContext.Response.Close(); // this breaks the connection, otherwise it may linger forever
							}
							catch (Exception) // HttpListenerResponse throws if the upgrade already touched it; the connection is dead either way
							{
							}
						}
						catch (Exception ex) when (TransportTeardown.IsExpected(ex)) // client vanished mid-upgrade -- benign teardown (contract: TransportTeardown.cs)
						{
							_logger.Log(EVerbosity.Debug, $"WebSocketServer.HandleConnection - websocket upgrade raced client teardown {SafeUrl(httpContext)} {ex.GetType().Name}: {ex.Message}");
							try
							{
								httpContext.Response.StatusCode = 500;
								httpContext.Response.Close();
							}
							catch (Exception) // same as above: the connection is dead either way
							{
							}
						}
						catch (Exception ex) // anything else
						{
							_logger.Log(EVerbosity.Error, $"WebSocketServer.HandleConnection - websocket upgrade exception {ex}");
							try
							{
								httpContext.Response.StatusCode = 500;
								httpContext.Response.Close(); // this breaks the connection, otherwise it may linger forever
							}
							catch (Exception) // HttpListenerResponse throws if the upgrade already touched it; the connection is dead either way
							{
							}
						}
					}
					finally
					{
						Interlocked.Decrement(ref _pendingUpgrades); // by now this socket is either registered in _liveSockets or gone entirely
					}
				}
				else // let the application specify what the HTTP response is, but we do the async write here to free up the app to do other things
				{
					using (CancellationTokenSource responseTimeout = new CancellationTokenSource(timeoutMS))
					{
						try
						{
							_logger.Log(EVerbosity.Debug, $"WebSocketServer.HandleConnection - normal http request {SafeUrl(httpContext)}");
							// The token goes INTO the callback so overrunning work can actually stop (and skip caching/writing),
							// and WaitAsync guards against a handler that ignores it.
							// Remember to set httpContext.Response.StatusCode, httpContext.Response.ContentLength64, and httpContenxtResponse.OutputStream
							await _httpRequestCallback(httpContext, responseTimeout.Token).WaitAsync(responseTimeout.Token).ConfigureAwait(false);
						}
						catch (OperationCanceledException) when (responseTimeout.IsCancellationRequested)
						{
							// The handler overran its deadline.  That is a SERVER-side fault (slow handler, or a stalled
							// client write holding it hostage) -- never routine teardown -- so it is loud, counted, and
							// answered honestly: 503 if the headers haven't gone out, a hard abort if they have.  Without
							// this, a timed-out request could surface to the client as a clean 200 with an empty body.
							// The when-filter matters: a handler that throws OperationCanceledException for its OWN reasons
							// is a handler exception (the loud catch below), not a deadline overrun.
							Metrics.RecordHttpHandlerTimeout();
							_logger.Log(EVerbosity.Warning, $"WebSocketServer.HandleConnection - http handler overran the {timeoutMS}ms deadline; abandoned and answered 503.  {SafeUrl(httpContext)}");
							bool sent503 = false;
							try
							{
								httpContext.Response.StatusCode = 503; // throws if the handler already sent headers -- then the truth is a hard abort, not a clean close of a half-written reply
								httpContext.Response.Close();
								sent503 = true;
							}
							catch (Exception) { }
							if (sent503 == false)
							{
								try
								{ httpContext.Response.Abort(); }
								catch (Exception) { }
							}
						}
						catch (Exception ex) when (TransportTeardown.IsExpected(ex)) // client went away mid-reply -- benign teardown (contract: TransportTeardown.cs)
						{
							_logger.Log(EVerbosity.Debug, $"WebSocketServer.HandleConnection - http callback closed by client {SafeUrl(httpContext)} {ex.GetType().Name}: {ex.Message}");
						}
						catch (Exception ex) // anything else, including a handler's own OperationCanceledException
						{
							_logger.Log(EVerbosity.Error, $"WebSocketServer.HandleConnection - http callback handler exception {ex}");
						}
						finally
						{
							// Close() frees this connection's memory, but on an already-disposed response (client hung up / listener
							// teardown) it throws -- and this is a finally, so an unguarded throw would escape HandleConnection.
							try
							{ httpContext.Response.Close(); }
							catch (Exception ex) when (TransportTeardown.IsExpected(ex)) { }
						}
					}
				}
			}

			// Hand every message to the manager raw -- RGConnectionManager's default OnRawMessage IS the typed pipeline, and
			// raw-mode managers override it and decode however they like (nothing is eagerly stringified here anymore).
			private Task OnReceive(RGWebSocket rgws, PooledArray msg, bool isText)
			{
				Metrics.RecordInboundMessage(msg.Length);
				return _connectionManager.OnRawMessage(rgws, msg, isText);
			}

			// Add this websocket to the list of those we need to remove and unblock the cleanup thread
			private async Task OnDisconnection(RGWebSocket rgws)
			{
				_logger.Log(EVerbosity.Debug, $"{rgws.DisplayId} OnDisconnection call.");
				try
				{
					await _connectionManager.OnDisconnect(rgws).ConfigureAwait(false); // let the connection manager know it's disconnected now
				}
				catch (Exception e)
				{
					_logger.Log(EVerbosity.Error, $"WebSocketServer.OnDisconnection Exception: {rgws.DisplayId} {e.Message}");
				}
				finally
				{
					// Hand the socket to the reaper for final disposal.  We are running ON this socket's send task right now, so awaiting
					// rgws.Shutdown() here would deadlock waiting for ourselves.  The reaper shuts it down from outside instead.
					// ORDER MATTERS: the reap count goes up BEFORE the socket leaves the live set, so StopListening's
					// combined (_liveSockets || _pendingReaps) wait can never observe a socket in neither.
					// The logging is isolated because it is APPLICATION code: a throwing ILogging here would skip the
					// bookkeeping below, leaving the socket in _liveSockets forever and wedging StopListening for 30s.
					try
					{ _logger.Log(EVerbosity.Debug, $"WebSocketServer.OnDisconnection queued for reaping {rgws.DisplayId}"); }
					catch (Exception) { }
					Interlocked.Increment(ref _pendingReaps);
					_liveSockets.Remove(rgws);         // no longer a candidate for the idle sweep
					Metrics.RecordDisconnect(rgws);    // fold this socket's lifetime stats into the distributions, tagged by cause
					if (_reapQueue.Add(rgws) == false) // reaper already exited (disconnect after full shutdown); nobody will take this
					{
						Interlocked.Decrement(ref _pendingReaps); // undo our own claim, or a later StopListening waits on a reap that can never happen
						try
						{ _logger.Log(EVerbosity.Error, $"WebSocketServer.OnDisconnection {rgws.DisplayId} disconnected after the reaper exited; it will not be disposed."); }
						catch (Exception) { }
					}
				}
			}
		}
	}
}