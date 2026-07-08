# RGWebSocket - WebSocket Library for C# and Unity

RGWebSocketUnity is a powerful and user-friendly C# library that simplifies the process of building WebSocket clients in C#, and is suitable for direct integration into Unity (.NET Standard 2.1).
RGWebSocket is a similarly powerful and user-friendly C# library for building high-performance and multi-threaded WebSocket servers in C# (.NET 10).  Both libraries reduce the complexities involved in handling WebSocket communications down to simple Connect/Disconnect/Send/Receive calls without you needing to worry about the details of handling WebSocket states.

## Features

- Provides a basic HTTP server that allows you to register functions to handle specific URL paths (exact and prefix matching).
- Automatically upgrades basic HTTP connections to WebSocket connections.
- Easy-to-use WebSocket client implementation for C# and Unity.
- Simplified WebSocket server creation in C# with multi-threading support.
- Text and binary messages flow through one pooled-buffer path -- no per-message allocations in the steady state.
- Abstracts away the complexities of WebSocket communication, allowing you to focus on your application logic.
- Compatible with standard WebSockets, since it is built on top of System.Net.WebSockets, including ws:// and wss:// protocols.

## Installation

You can install RGWebSocket via NuGet Package Manager or by downloading the source code from GitHub.

### NuGet Package Manager

To install RGWebSocket using NuGet, run the following command in the Package Manager Console:

```bash
Install-Package RGWebSocket
```

### Manual Installation from GitHub

1. Clone the RGWebSocket repository from GitHub.
2. Include the necessary RGWebSocket files in your C# or Unity project.

## Usage

### Logging

Both libraries log through a tiny `ILogging` interface, so you can route messages into whatever logging system your app uses:

```csharp
using Logging;

public class ConsoleLogger : ILogging
{
	public EVerbosity Verbosity { get; set; } = EVerbosity.Warning;
	public void Log(EVerbosity level, string msg) { if (level<=Verbosity) Console.WriteLine($"[{level}] {msg}"); }
	public void Dispose() {}
}
```

### WebSocket Client

RGWebSocket makes it incredibly easy to set up a WebSocket client in your C# or Unity project.  The client is poll-based: callbacks queue messages up on background threads, and you drain them from your main thread (e.g. a MonoBehaviour.Update).

```csharp
using Logging;
using ReachableGames.RGWebSocket;

ILogging logger = new ConsoleLogger();
Action<UnityWebSocket> disconnectCallback = (UnityWebSocket uws) => { Console.WriteLine("Disconnected"); };
int connectTimeoutMS = 3000;

// Create a new WebSocket client instance.  RGWebSocketConfig carries all the tunable limits (buffer sizes,
// inbound/outbound circuit breakers, idle disconnect); pass RGWebSocketConfig.Default if the defaults suit you.
UnityWebSocket client = new UnityWebSocket(logger, "Client", disconnectCallback, connectTimeoutMS, RGWebSocketConfig.Default);

// Connect to the WebSocket server
Dictionary<string, string> headers = new Dictionary<string, string>();
await client.Connect("wss://your-websocket-server.com", headers).ConfigureAwait(false);

if (client.IsConnected)
{
	// Send a text or binary message, does not block
	client.Send("Mary had a little lamb.");

	// Receive any waiting messages, does not block.  You own every message you receive and must dispose
	// each one to return its buffer to the pool.  isText tells you whether to read .Text or the raw bytes.
	List<UnityWebSocket.wsMessage> inboundMessages = new List<UnityWebSocket.wsMessage>();
	client.ReceiveAll(inboundMessages);
	foreach (UnityWebSocket.wsMessage m in inboundMessages)
	{
		using (m.msg)  // returns the pooled buffer when done
		{
			if (m.isText)
				Console.WriteLine(m.Text);
			else
				HandleBinary(m.msg.data, m.msg.Length);
		}
	}
	inboundMessages.Clear();

	// Kindly disconnect from the server (performs the close handshake)
	client.Close();

	// Forcibly abort whatever remains, release memory, and prepare for a new Connect/Reconnect call
	client.Shutdown();
}
```

### WebSocket Server

Creating a multi-threaded WebSocket server with RGWebSocket is just as simple.  Implement `IConnectionManager` to track your connections, then:

```csharp
using Logging;
using ReachableGames.RGWebSocket;

ILogging logger = new ConsoleLogger();
int listenerTasks = 20;
int connectionTimeoutMS = 3000;
int idleSeconds = 60;

// Make the connection manager, which is told when a new websocket is connected and is also
// responsible for closing them all if the server shuts down
IConnectionManager connectionMgr = new YourConnectionManager();

// Create a new WebSocket server instance on a port.  RGWebSocketConfig carries all the tunable limits; a game
// server probably wants the idle sweep on, so clients that vanish behind an Ingress get cleaned up:
RGWebSocketConfig config = new RGWebSocketConfig() { IdleDisconnectSeconds = 300 };  // clients must heartbeat within 5 minutes
IDataCollection? dataCollection = null;  // or pass your prometheus-backed IDataCollection derivative to have metrics pushed into it
WebServer webServer = new WebServer("http://+:24680/", listenerTasks, connectionTimeoutMS, idleSeconds, connectionMgr, logger, config, dataCollection);
webServer.RegisterExactEndpoint("/status", async (context) => (200, "text/plain", System.Text.Encoding.UTF8.GetBytes("OK")));

// Start listening.  This returns immediately; the listener runs on its own tasks.
webServer.Start();

// Run until you hit the X key
while (Console.ReadKey().KeyChar!='X') {}

// Stop the web server, which tells the IConnectionManager to shutdown all the connections
await webServer.Shutdown().ConfigureAwait(false);
```

## Threading contract

Knowing which thread runs your code is most of the battle with this kind of library, so here it is in one place:

- `RGWebSocket.Send()` (both overloads) is safe to call from **any thread** at any time.  Messages are queued and a dedicated send task drains them in order.
- `IConnectionManager.OnReceiveText`/`OnReceiveBinary` run on **that socket's receive task**.  One message at a time per socket, but different sockets call you **concurrently** -- anything they touch that is shared must be thread-safe.  Do not block in these callbacks (no sync waits on other locks, DBs, etc); you stall that socket's receive pump and, at scale, the thread pool.
- `IConnectionManager.OnConnection` runs before the socket's pumps start -- nothing can arrive until you return.
- `IConnectionManager.OnDisconnect` runs on **the dying socket's send task**.  Never call `RGWebSocket.Shutdown()` from there (it would wait for itself); `WebSocketServer` hands the socket to a reaper task that disposes it from outside.
- `UnityWebSocket` queues everything; you drain with `ReceiveAll()` from **your main thread** whenever you like.  The `disconnectCallback` fires on the socket's send task -- set flags, don't touch your world state from it.
- `WebServer` HTTP endpoint handlers run on thread pool tasks, one per request, bounded by the connection timeout.

## Disconnect causes and metrics

Every socket records **why** it died (`RGWebSocket.DisconnectReason`): remote close (with the peer's close status/description), transport error, the outbound/inbound circuit breakers, idle timeout, user code exception, or local close/shutdown.  `WebSocketServer.Metrics` aggregates distribution-oriented server metrics -- current/high-water concurrents, disconnect counts by cause, and power-of-two histograms (count/min/mean/p50/p90/p99/max) for inbound message sizes, per-socket lifetime send/receive counts and bytes, and connection duration.  Wire `Metrics.Report()` to an HTTP endpoint via `RegisterExactEndpoint` and you have a health page.

For prometheus/Grafana, implement `IDataCollection` (gauges, counters, histograms) and pass it to `WebServer`/`WebSocketServer`; the library registers `rgws_*` metrics at startup and pushes events as they happen (connect/disconnect gauges and per-cause counters, message-size and per-socket-lifetime histograms, connection duration in seconds).  Your derivative owns `Generate()`; the library never formats a metrics page itself.

## Testing

The repository contains a chat-style stress test (`ChatTest`) that spins up a server and dozens of in-process clients that broadcast, whisper, exchange binary blobs, disconnect gracefully, die abruptly, and reconnect -- then verifies that no connections or pooled buffers leak:

```bash
dotnet run --project ChatTest -- clients=48 seed=12345
```

## Documentation

I wish there was more documentation for this library, but I haven't had time to write any yet.  It's still in development, although I suspect the rate of change is slowing.  The best guide for now is code in WebServer.cs and UnityWebSocket.cs and IConnectionManager.cs, the ChatTest example, or ask questions.

## Contributing

Although we welcome contributions to RGWebSocket, this is being used in several live products so bug fixes are all that can be considered for inclusion at present.  If you have any suggestions for improvements, feel free to reach out.

## License

RGWebSocket is released under the [MIT License](https://opensource.org/licenses/MIT).
