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

There are two ways to consume RGWebSocket, and they build from the same two source folders:

| Folder | What it is | Ends up in |
|---|---|---|
| `RGWebSocket.Core/` | Client + shared code (netstandard2.1-safe, works in Unity) | **Every** consuming project — client or server. |
| `RGWebSocket.Server/` | Server-side code: RGWebSocketServer, RGWebServer, RGConnectionManager, metrics (.NET 10) | Server projects only, in addition to Core. |

Everything else in the repo (the root csproj/sln files, `ChatTest/`, the batch files) is build/test scaffolding and is never consumed anywhere.

### Option A: prebuilt DLL + PDB drop-in (recommended)

Run `build.bat`.  It produces two **standalone, self-contained** folders under `build/` — copy one folder wholesale into your project:

| Folder | Contents | Drop into |
|---|---|---|
| `build/Server/` | `RGWebSocket.dll` + `RGWebSocket.pdb` (Core **and** Server, .NET 10) | A .NET server project — reference the DLL directly. |
| `build/Client/` | `RGWebSocketUnity.dll` + `RGWebSocketUnity.pdb` + `System.Threading.Channels.dll` (Core only, netstandard2.1) | Unity `Assets/Plugins/`. |

Pick exactly one: **clients take the Client folder, servers take the Server folder.**  There is no NuGet feed and no cross-package dependency to manage — each folder is everything that package needs.

Every DLL ships with a **portable PDB that has the full library source embedded**, so you can step straight into RGWebSocket's code from your project with nothing but the `.dll` + `.pdb` present — no source tree, no SourceLink, no symbol server.  (To actually step in, build your project in Debug or turn off "Just My Code" in the debugger, since the DLL itself is an optimized Release build.)

The server DLL is built with `IsAotCompatible=true`, so it is verified trim/NativeAOT-clean at library build time and is safe to reference from a NativeAOT server project.

Unity's BCL does not include `System.Threading.Channels`, which is why the Client folder ships that DLL alongside — drop all three files into `Assets/Plugins/` and you are done.  No NuGet involvement.

### Option B: copy the source folders

Prefer to compile the library as part of your own assembly?  Copy `RGWebSocket.Core/` (and `RGWebSocket.Server/` for servers) into your project tree — the SDK-style `**/*.cs` glob picks them up automatically.  For Unity, copy `RGWebSocket.Core/` into `Assets/` and add `System.Threading.Channels.dll` (from `build/Client/`, or the NuGet package of the same name) to `Assets/Plugins/`.

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
Action<RGUnityWebSocket> disconnectCallback = (RGUnityWebSocket uws) => { Console.WriteLine("Disconnected"); };
int connectTimeoutMS = 3000;

// Create a new WebSocket client instance.  This is the raw-mode constructor; pass an IMessageFactory as the last
// argument instead to get the typed Send(IRGMessage)/ReceiveAll(List<IRGMessage>) API (recommended, see below).
RGUnityWebSocket client = new RGUnityWebSocket(logger, "Client", disconnectCallback, connectTimeoutMS);

// Connect to the WebSocket server
Dictionary<string, string> headers = new Dictionary<string, string>();
await client.Connect("wss://your-websocket-server.com", headers).ConfigureAwait(false);

if (client.IsConnected)
{
	// Send a text or binary message, does not block
	client.Send("Mary had a little lamb.");

	// Receive any waiting messages, does not block.  You own every message you receive and must dispose
	// each one to return its buffer to the pool.  isText tells you whether to read .Text or the raw bytes.
	List<RGUnityWebSocket.wsMessage> inboundMessages = new List<RGUnityWebSocket.wsMessage>();
	client.ReceiveAll(inboundMessages);
	foreach (RGUnityWebSocket.wsMessage m in inboundMessages)
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

Creating a multi-threaded WebSocket server with RGWebSocket is just as simple.  Derive from `RGConnectionManager` to track your connections, then:

```csharp
using Logging;
using ReachableGames.RGWebSocket;

ILogging logger = new ConsoleLogger();
int listenerTasks = 20;
int connectionTimeoutMS = 3000;
int idleSeconds = 60;

// Make the connection manager, which is told when a new websocket is connected and is also
// responsible for closing them all if the server shuts down.  Derive from RGConnectionManager:
// pass an IMessageFactory to its base constructor for typed messages (recommended), or use the
// logger-only base constructor and override OnRawMessage to speak your own protocol.
RGConnectionManager connectionMgr = new YourConnectionManager(new YourMessageFactory(), logger);

// RGWebSocketConfig holds process-wide tunables (buffer sizes, inbound/outbound circuit breakers, idle sweep) as
// getter-only statics.  Call Configure() ONCE before creating anything websocket-related (it validates and throws on
// nonsense; it also throws if called after any socket/server exists), or skip it entirely to accept the defaults.
// A game server probably wants the idle sweep on, so clients that vanish behind an Ingress get cleaned up -- clients
// must then heartbeat more often than that interval (see the notes in RGWebSocketConfig.cs):
RGWebSocketConfig.Configure(receiveBufferBytes: 4096, maxInboundMessageBytes: 16*1024*1024, maxUnsentBytes: 4*1024*1024, idleDisconnectSeconds: 300, idleSweepPeriodSeconds: 60);

// Create a new WebSocket server instance on a port.
IDataCollection? dataCollection = null;  // or pass your prometheus-backed IDataCollection derivative to have metrics pushed into it
WebServer webServer = new WebServer("http://+:24680/", listenerTasks, connectionTimeoutMS, idleSeconds, connectionMgr, logger, dataCollection);
webServer.RegisterExactEndpoint("/status", async (context) => (200, "text/plain", System.Text.Encoding.UTF8.GetBytes("OK")));

// Start listening.  This returns immediately; the listener runs on its own tasks.
webServer.Start();

// Run until you hit the X key
while (Console.ReadKey().KeyChar!='X') {}

// Stop the web server, which tells the RGConnectionManager to shutdown all the connections
await webServer.Shutdown().ConfigureAwait(false);
```

## Typed messages (recommended)

`RGConnectionManager` (server) and `RGUnityWebSocket` (client) deal in strongly typed message objects by default.
Your messages implement `IRGMessage` (one property: an integer `TypeId` -- dispatch is a switch, never reflection, so
IL2CPP/AOT is happy).  Your codec implements `IMessageFactory`, which owns BOTH `Serialize` and `Deserialize` -- the
codec is a swappable strategy, so the same message classes can ride a sloppy JSON factory during development and a
packed binary factory in production by changing one constructor argument.  See `ChatTest/TypedPhase.cs` for a complete
working example of both codecs.

All pooled-buffer accounting stays inside the library: `Deserialize` receives a `ReadOnlySpan<byte>`, so the compiler
itself guarantees no reference to pooled memory can escape, and the typed `ReceiveAll` has no disposal contract at all.
Deserialization runs on each socket's receive task, so parsing parallelizes across connections.  The typed pipeline is
binary-only and strict: text frames, runt frames, unknown type ids, or payloads the factory rejects disconnect the peer
with `EDisconnectReason.ProtocolError` (visible by cause in the metrics).  Wire format: `[int32 LE type id][payload]`.

If you need text frames or your own framing, use the logger-only `RGConnectionManager` constructor and override
`OnRawMessage(rgws, PooledArray, isText)` -- you own the protocol and the buffer rules apply (the library releases the
buffer when you return; `IncRef` it if you keep it).  `ChatTest/ChatServer.cs` is a working raw-mode example.  On the
client, `RGUnityWebSocket`'s config-only constructor is raw mode with the `wsMessage` API.

## Threading contract

Knowing which thread runs your code is most of the battle with this kind of library, so here it is in one place:

- `RGWebSocket.Send()` (both overloads) is safe to call from **any thread** at any time.  Messages are queued and a dedicated send task drains them in order.
- `RGConnectionManager.OnMessage` (and `OnRawMessage`, if you override it) runs on **that socket's receive task**.  One message at a time per socket, but different sockets call you **concurrently** -- anything they touch that is shared must be thread-safe.  Do not block in these callbacks (no sync waits on other locks, DBs, etc); you stall that socket's receive pump and, at scale, the thread pool.
- `RGConnectionManager.OnConnection` runs before the socket's pumps start -- nothing can arrive until you return.
- `RGConnectionManager.OnDisconnect` runs on **the dying socket's send task**.  Never call `RGWebSocket.Shutdown()` from there (it would wait for itself); `WebSocketServer` hands the socket to a reaper task that disposes it from outside.
- `RGUnityWebSocket` queues everything; you drain with `ReceiveAll()` from **your main thread** whenever you like.  The `disconnectCallback` fires on the socket's send task -- set flags, don't touch your world state from it.
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

I wish there was more documentation for this library, but I haven't had time to write any yet.  It's still in development, although I suspect the rate of change is slowing.  The best guide for now is code in WebServer.cs, RGConnectionManager.cs, and RGUnityWebSocket.cs, the ChatTest example, or ask questions.

## Contributing

Although we welcome contributions to RGWebSocket, this is being used in several live products so bug fixes are all that can be considered for inclusion at present.  If you have any suggestions for improvements, feel free to reach out.

## License

RGWebSocket is released under the [MIT License](https://opensource.org/licenses/MIT).
