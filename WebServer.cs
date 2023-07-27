//-------------------
// Reachable Games
// Copyright 2023
//-------------------

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		// Use this to easily register endpoints for callbacks for normal HTTP requests, whereas all websocket upgrades will be handled by the IConnectionManager that is passed in.
		public class WebServer
		{
			private readonly string              _url;
			private readonly string              _urlPath;  // if this server is hosted at http://some.com/foo/bar then this variable will contain foo/bar for easy removal
			private readonly int                 _listenerThreads;
			private readonly int                 _connectionTimeoutMS;
			private readonly int                 _idleSeconds;
			private readonly OnLogDelegate       _logger;
			private readonly IConnectionManager  _connectionManager;
			private CancellationTokenSource      _cancellationTokenSrc = null;  // this gets allocated and destroyed based on server status being listening or not.

			//-------------------

			public WebServer(string url, int listenerThreads, int connectionTimeoutMS, int idleSeconds, IConnectionManager connectionManager, OnLogDelegate logger)
			{
				_url                 = url;
				_listenerThreads     = listenerThreads;
				_connectionTimeoutMS = connectionTimeoutMS;
				_idleSeconds         = idleSeconds;
				_connectionManager   = connectionManager;
				_logger              = logger;

				string[] urlParts = url.Split('/');  // When you have a url, you have protocol://domain:port/path/part/etc
				_urlPath          = string.Join('/', urlParts, 3, urlParts.Length-3);  // this leaves you with path/part/etc
			}

			//-------------------

			public async Task Start()
			{
				if (_cancellationTokenSrc!=null)
					throw new Exception("WebServer cannot be started multiple times without Shutdown being called.");

				_cancellationTokenSrc = new CancellationTokenSource();
				using (_cancellationTokenSrc)
				{
					using (WebSocketServer httpServer = new WebSocketServer(_listenerThreads, _connectionTimeoutMS, _idleSeconds, _url, HttpRequestHandler, _connectionManager, _logger))
					{
						try
						{
							httpServer.StartListening();  // start listening AFTER we have registered the handlers

							// Since the main program passed in the cancellation token, it literally controls the completion of this task,
							// which only happens when told to shut down with ^C or SIGINT.
							await _cancellationTokenSrc.Token;  // magic!
						}
						catch (OperationCanceledException)
						{
							_logger(ELogVerboseType.Error, "Canceling WebServer.");
						}
						catch (Exception e)
						{
							if (e is HttpListenerException)
							{
								_logger(ELogVerboseType.Error, "If you get an Access Denied error, open an ADMIN command shell and run:");
								_logger(ELogVerboseType.Error, $"   netsh http add urlacl url={_url} user=\"{Environment.UserDomainName}\\{Environment.UserName}\"");
							}
							else
							{
								_logger(ELogVerboseType.Error, $"Exception: {e}");
							}
						}
						finally
						{
							await httpServer.StopListening().ConfigureAwait(false);  // kill all the connections and abort any that don't die quietly
							_logger(ELogVerboseType.Warning, "WebServer has shutdown");
						}
					}
				}
				_cancellationTokenSrc = null;
			}

			public void Shutdown()
			{
				_cancellationTokenSrc?.Cancel();
				_logger(ELogVerboseType.Error, "WebServer shutdown requested");
			}

			//-------------------
			// HTTP handlers
			//-------------------
			// This is the set of http endpoint handlers are kept.  "/metrics" -> Metrics.HandleMetricsRequest, for example.
			public delegate Task HTTPRequestHandler(HttpListenerContext context);
			private Dictionary<string, HTTPRequestHandler> _endpointHandlers = new Dictionary<string, HTTPRequestHandler>();

			public void RegisterEndpoint(string urlPath, HTTPRequestHandler handler)
			{
				if (_endpointHandlers.TryAdd(urlPath, handler) == false)
				{
					_logger(ELogVerboseType.Error, $"RegisterEndpoint {urlPath} is already defined.  Ignoring.");
				}
			}

			public void UnregisterEndpoint(string urlPath)
			{
				if (_endpointHandlers.Remove(urlPath) == false)
				{
					_logger(ELogVerboseType.Error, $"UnregisterEndpoint {urlPath} not found to unregister.");
				}
			}

			// Regular HTTP calls come here.  They are dispatched to any registered endpoints.
			private async Task HttpRequestHandler(HttpListenerContext httpContext)
			{
				string path = httpContext.Request.Url?.AbsolutePath ?? string.Empty;
				string relativeEndpoint = string.IsNullOrEmpty(_urlPath) ? path : path.Replace(_urlPath, string.Empty);
				if (_endpointHandlers.TryGetValue(relativeEndpoint, out HTTPRequestHandler handler))
				{
					try
					{
						await handler(httpContext).ConfigureAwait(false);
					}
					catch (Exception e)
					{
						byte[] buffer = System.Text.Encoding.UTF8.GetBytes($"Exception {httpContext.Request.Url?.ToString() ?? string.Empty} {e}");
						httpContext.Response.StatusCode = 500;
						httpContext.Response.ContentLength64 = buffer.Length;
						await httpContext.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
					}
				}
				else
				{
					byte[] buffer = System.Text.Encoding.UTF8.GetBytes($"No endpoint found for {httpContext.Request.Url?.ToString() ?? string.Empty}");

					httpContext.Response.StatusCode = 404;
					httpContext.Response.ContentLength64 = buffer.Length;
					await httpContext.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
				}
			}
		}
	}
}