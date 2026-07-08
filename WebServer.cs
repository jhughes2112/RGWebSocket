#nullable enable
﻿//-------------------
// Reachable Games
// Copyright 2023
//-------------------

using Logging;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		// Use this to easily register endpoints for callbacks for normal HTTP requests, whereas all websocket upgrades will be handled by the IConnectionManager that is passed in.
		public class WebServer
		{
			private readonly string              _url;
			private readonly string              _urlPath;        // if this server is hosted at http://some.com/foo/bar then this variable will contain foo/bar for easy removal
			private readonly string              _urlPathPrefix;  // "/foo/bar/" form of the above, for prefix-stripping request paths
			private readonly ILogging            _logger;
			private WebSocketServer              _httpServer;

			//-------------------

			public WebServer(string url, int listenerThreads, int connectionTimeoutMS, int idleSeconds, IConnectionManager connectionManager, ILogging logger)
			{
				_url                 = url;
				_logger              = logger;

				string[] urlParts = url.Split('/');  // When you have a url, you have protocol://domain:port/path/part/etc
				_urlPath          = string.Join('/', urlParts, 3, urlParts.Length-3);  // this leaves you with path/part/etc
				_urlPathPrefix    = "/" + _urlPath;
				if (_urlPathPrefix.EndsWith("/", StringComparison.Ordinal)==false)
					_urlPathPrefix += "/";

				_httpServer = new WebSocketServer(listenerThreads, connectionTimeoutMS, idleSeconds, _url, HttpRequestHandler, connectionManager, _logger);
			}

			//-------------------

			public void Start()
			{
				if (_httpServer.IsListening())
					throw new Exception($"WebServer.Start is already listening at {_url}");

				try
				{
					_httpServer.StartListening();  // start listening AFTER we have registered the handlers
				}
				catch (Exception e)
				{
					if (e is HttpListenerException)
					{
						_logger.Log(EVerbosity.Error, "If you get an Access Denied error, open an ADMIN command shell and run:");
						_logger.Log(EVerbosity.Error, $"   netsh http add urlacl url={_url} user=\"{Environment.UserDomainName}\\{Environment.UserName}\"");
					}
					else
					{
						_logger.Log(EVerbosity.Error, $"Exception: {e}");
					}
					throw;
				}
				_logger.Log(EVerbosity.Warning, $"WebServer.Start listening at {_url}");
			}

			public async Task Shutdown()
			{
				await _httpServer.StopListening().ConfigureAwait(false);  // kill all the connections and abort any that don't die quietly
				_logger.Log(EVerbosity.Error, $"WebServer.Shutdown at {_url}");
			}

			//-------------------
			// HTTP handlers
			//-------------------
			// This is the set of http endpoint handlers are kept.  "/metrics" -> Metrics.HandleMetricsRequest, for example.
			public delegate Task<(int, string, byte[])> HTTPRequestHandler(HttpListenerContext context);  // handlers should return (httpStatus, contentType, content) so we can handle errors gracefully
			
			// Exact match endpoints - use dictionary for fast O(1) lookup
			private Dictionary<string, HTTPRequestHandler> _exactEndpointHandlers = new Dictionary<string, HTTPRequestHandler>();
			
			// Prefix match endpoints - use list for ordered checking, stored as (prefix, handler) tuples
			private List<(string prefix, HTTPRequestHandler handler)> _prefixEndpointHandlers = new List<(string, HTTPRequestHandler)>();

			// Register an endpoint that matches exactly
			public void RegisterExactEndpoint(string urlPath, HTTPRequestHandler handler)
			{
				if (_exactEndpointHandlers.TryAdd(urlPath, handler) == false)
				{
					_logger.Log(EVerbosity.Error, $"RegisterExactEndpoint {urlPath} is already defined.  Ignoring.");
				}
			}

			// Register an endpoint that matches if the request path starts with the given prefix
			public void RegisterPrefixEndpoint(string urlPrefix, HTTPRequestHandler handler)
			{
				// Check if this prefix is already registered
				for (int i = 0; i < _prefixEndpointHandlers.Count; i++)
				{
					if (_prefixEndpointHandlers[i].prefix == urlPrefix)
					{
						_logger.Log(EVerbosity.Error, $"RegisterPrefixEndpoint {urlPrefix} is already defined.  Ignoring.");
						return;
					}
				}
				
				_prefixEndpointHandlers.Add((urlPrefix, handler));
			}

			// Unregister an exact endpoint
			public void UnregisterExactEndpoint(string urlPath)
			{
				if (_exactEndpointHandlers.Remove(urlPath) == false)
				{
					_logger.Log(EVerbosity.Error, $"UnregisterExactEndpoint {urlPath} not found to unregister.");
				}
			}

			// Unregister a prefix endpoint
			public void UnregisterPrefixEndpoint(string urlPrefix)
			{
				for (int i = 0; i < _prefixEndpointHandlers.Count; i++)
				{
					if (_prefixEndpointHandlers[i].prefix == urlPrefix)
					{
						_prefixEndpointHandlers.RemoveAt(i);
						return;
					}
				}
				_logger.Log(EVerbosity.Error, $"UnregisterPrefixEndpoint {urlPrefix} not found to unregister.");
			}

			// Regular HTTP calls come here.  They are dispatched to any registered endpoints.
			private async Task HttpRequestHandler(HttpListenerContext httpContext)
			{
				int     responseCode = 500;
				string  responseContentType = "text/plain";
				byte[]? responseContent = null;

				// Strip the hosting prefix off the FRONT of the path only.  (string.Replace would also mangle it if it appeared mid-path,
				// e.g. hosting at /api would break a request for /api/api-docs.)
				string path = httpContext.Request.Url?.AbsolutePath ?? string.Empty;
				string relativeEndpoint = path;
				if (_urlPathPrefix.Length>1)
				{
					if (path.StartsWith(_urlPathPrefix, StringComparison.Ordinal))
						relativeEndpoint = path.Substring(_urlPathPrefix.Length-1);  // keep the leading slash, e.g. /foo/metrics -> /metrics
					else if (path.Length==_urlPathPrefix.Length-1 && _urlPathPrefix.StartsWith(path, StringComparison.Ordinal))
						relativeEndpoint = "/";  // a request for the hosting root itself, without the trailing slash
				}
				
				HTTPRequestHandler? handler = null;
				
				// First, try exact match (fastest - O(1) dictionary lookup)
				if (_exactEndpointHandlers.TryGetValue(relativeEndpoint, out handler))
				{
					// Found exact match
				}
				else
				{
					// If no exact match, check prefix matches (slower - O(n) list iteration)
					foreach (var (prefix, prefixHandler) in _prefixEndpointHandlers)
					{
						if (relativeEndpoint.StartsWith(prefix))
						{
							handler = prefixHandler;
							break; // Use the first matching prefix
						}
					}
				}
				
				if (handler != null)
				{
					try
					{
						(responseCode, responseContentType, responseContent) = await handler(httpContext).ConfigureAwait(false);
					}
					catch (Exception e)
					{
						// Log the details, but never send exception text (stack frames, paths, internals) to whoever is on the other end of the socket.
						_logger.Log(EVerbosity.Error, $"Exception in endpoint handler {httpContext.Request.Url?.ToString() ?? string.Empty} {e}");
						responseCode = 500;
						responseContentType = "text/plain";
						responseContent = System.Text.Encoding.UTF8.GetBytes("500 Internal Server Error");
					}
				}
				else
				{
					responseCode = 404;
					responseContentType = "text/plain";
					responseContent = System.Text.Encoding.UTF8.GetBytes($"No endpoint found for {httpContext.Request.Url?.ToString() ?? string.Empty}");
				}

				try
				{
					httpContext.Response.ContentType = responseContentType;
					httpContext.Response.StatusCode = responseCode;
					if (responseContent!=null)
					{
						httpContext.Response.ContentLength64 = responseContent.Length;
						await httpContext.Response.OutputStream.WriteAsync(responseContent, 0, responseContent.Length).ConfigureAwait(false);
					}
				}
				catch (Exception e)
				{
					_logger.Log(EVerbosity.Error, $"Exception while trying to write to http response.  {httpContext.Request.Url?.ToString() ?? string.Empty} {e}");
				}
			}
		}
	}
}