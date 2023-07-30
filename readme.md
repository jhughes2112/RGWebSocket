# RGWebSocket - WebSocket Library for C# and Unity

RGWebsocketUnity is a powerful and user-friendly C# library that simplifies the process of building WebSocket clients in C#, and is suitable for direct integration into Unity.
RGWebSocket is a similarly powerful and user-friendly C# library for building high-performance and multi-threaded WebSocket servers in C#.  Both libraries reduce the complexities involved in handling WebSocket communications down to simple Connect/Disconnect/Send/Receive calls without you needing to worry about the details of handling WebSocket states.

## Features

- Provides a basic HTTP server that allows you to register functions to handle specific URL paths.
- Automatically upgrades basic HTTP connections to WebSocket connections.
- Easy-to-use WebSocket client implementation for C# and Unity.
- Simplified WebSocket server creation in C# with multi-threading support.
- Abstracts away the complexities of WebSocket communication, allowing you to focus on your application logic.
- Compatible with standard WebSockets, since it is built on top of System.Net.WebSocket, including ws:// and wss:// protocols.

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

### WebSocket Client

RGWebSocket makes it incredibly easy to set up a WebSocket client in your C# or Unity project. Follow these simple steps:

```csharp
using ReachableGames.RGWebSocket;

OnLogDelegate logger = (ELogVerboseType lvl, string msg) => { Console.WriteLine(msg) };
Action<UnityWebSocket> disconnectCallback = (UnityWebSocket uws) => { Console.WriteLine("Disconnected" };
int connectTimeoutMs = 3000;

// Create a new WebSocket client instance
UnityWebSocket client = UnityWebSocket(logger, "Client", disconnectCallback, connectTimeoutMs);

// Connect to the WebSocket server
Dictionary<string, string> headers = new Dictionary<string, string>();
await client.Connect("wss://your-websocket-server.com", headers).ConfigureAwait(false);

if (client.IsConnected)
{
	// Send a message, does not block
	client.Send("Mary had a little lamb.");

	// Receive any waiting messages, does not block
	List<UnityWebSocket.wsMessage> inboundMessages = new List<UnityWebSocket.wsMessage>();
	client.ReceiveAll(inboundMessages);

	// Kindly disconnect from the server
	client.Close();
	
	// Forcibly abort the connection, release memory, and prepare for new a Connect call
	client.Shutdown();
}
```

### WebSocket Server

Creating a multi-threaded WebSocket server with RGWebSocket is just as simple:

```csharp
using ReachableGames.RGWebSocket;

OnLogDelegate logger = (ELogVerboseType lvl, string msg) => { Console.WriteLine(msg) };
int listenerTasks = 20;
int connectionTimeoutMs = 3000;
int idleSeconds = 60;

// Make the connection manager, which is told when a new websocket is connected and is also 
// responsible for closing them if the server shuts down
IConnectionManager connectionMgr = new YourConnectionManager();

// Create a new WebSocket server instance on port
WebServer webServer = new WebServer("http://+:24680/", listenerTasks, connectionTimeoutMS, idleSeconds, connectionMgr, logger);

// Start the WebSocket server and run it until you hit the X key
Task webTask = Task.Run(webServer.Start);
while (Console.ReadKey().KeyChar!='X') {}

// Stop the web server, which tells the IConnectionManager to shutdown all the connections
webServer.Shutdown();
await webTask.ConfigureAwait(false);
```

## Documentation

I wish there was more documentation for this library, but I haven't had time to write any yet.  It's still in development, although I suspect the rate of change is slowing.  The best guide for now is code in WebServer.cs and UnityWebSocket.cs and IConnectionManager.cs or ask questions.

## Contributing

Although we welcome contributions to RGWebSocket, this is being used in several live products so bug fixes are all that can be considered for inclusion at present.  If you have any suggestions for improvements, feel free to reach out.

## License

RGWebSocket is released under the [MIT License](https://opensource.org/licenses/MIT).

