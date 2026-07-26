# RGWebSocket -- API Reference

This file rides along with the prebuilt DLLs so you can write code against them without the repository.
The `.pdb` beside each DLL has the **full source embedded** -- to step into library code from your own
project, run your project as Debug and disable Just My Code; no source checkout needed.

| Package | DLL | Target | Contains |
|---|---|---|---|
| Server | `RGWebSocket.dll` | net10.0 (AOT-clean) | Everything below (client classes included, usable for server-to-server links) |
| Client | `RGWebSocketUnity.dll` + `System.Threading.Channels.dll` | netstandard2.1 (Unity-safe) | Client + shared types only; no server classes |

Namespaces: `ReachableGames.RGWebSocket` (everything), `Logging` (ILogging), `DataCollection` (IDataCollection, server only).

---

## 0. Before anything else: configuration and logging

```csharp
// OPTIONAL, but if you call it, call it ONCE, before creating any socket/server/client.
// It validates hard and THROWS after anything is constructed (the pumps read these unsynchronized).
// Skip the call entirely to accept the defaults shown here.
RGWebSocketConfig.Configure(
    receiveBufferBytes:        4096,             // initial recv buffer; grows by doubling per message
    maxInboundMessageBytes:    16 * 1024 * 1024, // one message bigger than this = disconnect (InboundOversize)
    maxUnsentBytes:            4 * 1024 * 1024,  // outbound queue budget; a consumer slower than this = disconnect (OutboundBackpressure)
    maxRecvQueueBytes:         8 * 1024 * 1024,  // client-side inbound backlog budget; stalled ReceiveAll = disconnect (InboundBacklog).  0 = off
    idleDisconnectSeconds:     0,                // server sweep: disconnect sockets that RECEIVED nothing for this long.  0 = off
    idleSweepPeriodSeconds:    60,               // how often the sweep wakes
    webSocketKeepAliveSeconds: 30,               // protocol ping interval, both sides.  0 = off.  1-4s is rejected (packet storm)
    maxConcurrentWebSockets:   0);               // server cap; over it, upgrades get 503.  0 = off
```

Everything takes an `ILogging` (namespace `Logging`) -- you implement it:

```csharp
public interface ILogging : IDisposable
{
    EVerbosity Verbosity { get; set; }        // Error=0, Warning=1, Info=2, Debug=3, Extreme=4
    void Log(EVerbosity level, string msg);
}
```

**Heartbeats (read this, it will bite you):** protocol pings are answered inside .NET and NEVER count as
received data, so they never refresh the idle sweep. If you enable `idleDisconnectSeconds`, clients must
send a real message more often than that. A zero-length binary message is a legal, cheap heartbeat.

---

## 1. Client: `RGUnityWebSocket`

Built for a game loop: background pumps do the socket work, you poll from your main thread.

```csharp
// TYPED mode (recommended) -- no buffer management in your code at all:
var ws = new RGUnityWebSocket(logger, "[game]", onDisconnect: null, connectTimeoutMS: 5000, factory);
await ws.Connect("wss://example.com/play", new Dictionary<string,string>()); // headers, e.g. auth token
ws.Send(new MoveMsg { X = 3 });                    // bool return: false if not connected
var inbox = new List<IRGMessage>();
void Update() {                                    // every frame / tick:
    ws.ReceiveAll(inbox);                          // drains everything pending; malformed frames are logged+skipped
    foreach (IRGMessage m in inbox) Handle(m);     // nothing to dispose in typed mode
    inbox.Clear();
}
await ws.ShutdownAsync();                          // full teardown; Connect/Reconnect legal again afterward
```

```csharp
// RAW mode (omit the factory) -- you own the protocol AND the buffers:
var ws = new RGUnityWebSocket(logger, "[raw]", null, 5000);
ws.Send("text frame");                             // strings go as text frames
using (PooledArray buf = PooledArray.BorrowFromPool(n)) { /* fill buf.data[0..n) */ ws.Send(buf); }
var msgs = new List<RGUnityWebSocket.wsMessage>();
ws.ReceiveAll(msgs);                               // YOU own every message now:
foreach (var m in msgs) using (m.msg) { Use(m.msg.data, m.msg.Length, m.isText); } // m.Text decodes UTF8
msgs.Clear();
```

The rest of the surface: `Reconnect()` (reuses the last url/headers), `Close()` (graceful close handshake;
poll `IsDisconnected`), `Shutdown()` (blocking wrapper over ShutdownAsync -- fine at app exit, never in a
game loop), `IsConnected` / `IsConnecting` / `IsDisconnected`, `LastError`, `DisconnectReason`,
`GetStats(out sent, out sentBytes, out recv, out recvBytes)`.

**Rules that are not optional:**
- The `disconnectCallback` runs on the dying socket's own send task. **Never** call `Shutdown`,
  `Connect`, or `Reconnect` from inside it -- that waits for the very task running your callback, and
  hangs forever. Set a flag; act on it from your update loop.
- Call `ReceiveAll` regularly. The inbound queue is bounded by `maxRecvQueueBytes`; if you stop draining
  it, the library disconnects you (`InboundBacklog`) rather than eating unbounded memory.

---

## 2. Server: derive `RGConnectionManager`, host with `RGWebServer`

```csharp
public class MyManager : RGConnectionManager
{
    public MyManager(IMessageFactory factory, ILogging logger) : base(factory, logger) { }  // typed mode

    public override Task OnConnection(RGWebSocket rgws, HttpListenerContext ctx) { /* track rgws */ return Task.CompletedTask; }
    public override Task OnMessage   (RGWebSocket rgws, IRGMessage msg)          { /* your logic  */ return Task.CompletedTask; }
    public override Task OnDisconnect(RGWebSocket rgws)                          { /* untrack     */ return Task.CompletedTask; }
    public override Task Shutdown    ()                                          { /* Close() every socket you track */ return Task.CompletedTask; }
}
// Reply from anywhere:  Send(rgws, new StateMsg {...});   (base-class method, thread-safe)
```

```csharp
var server = new RGWebServer(
    "http://+:8080/",        // HttpListener prefix.  https requires an OS cert binding (netsh) -- not the library's job
    listenerThreads: 4,      // concurrent accept tasks
    connectionTimeoutMS: 5000,  // deadline for upgrades, authorizers, and http handlers
    idleSeconds: 30,         // plain-HTTP connection idle timeout (does not affect websockets)
    manager, logger,
    dataCollection: null,    // or your IDataCollection for prometheus -- see section 5
    upgradeAuthorizer: null);// or your pre-handshake gate -- see below
server.Start();
...
await server.Shutdown();     // closes+drains every socket, then stops the listener.  Bounded (~30s worst case)
```

Callback contract (each runs where it says; this is the whole threading story):
- `OnConnection` -- before the socket's pumps start; nothing can arrive until you return.
- `OnMessage` -- on that socket's receive task. One message at a time per socket; different sockets are
  concurrent. Blocking here stalls only that socket. Throwing kills only that connection (`UserCodeException`).
- `OnDisconnect` -- on the dying socket's send task. Same rule as the client: never await `Shutdown()`
  here. The library reaps and disposes the socket for you afterward.
- Wrong-protocol peers (text frame in typed mode, runt frame, unknown type id, payload the factory
  rejects/throws on) are disconnected as `ProtocolError` automatically. You never see them.

**Raw mode:** construct with `base(logger)` only, override
`protected Task OnRawMessage(RGWebSocket rgws, PooledArray msg, bool isText)`, stub `OnMessage`.
The buffer is released when your override returns -- `msg.IncRef()` + `using` later if you keep it.

**Pre-upgrade gate** (Origin checks / auth -- browsers let ANY site open a websocket to you; this is the defense):

```csharp
// Runs BEFORE the 101 handshake.  null = admit; a tuple = refuse with that plain HTTP response.
// Throwing fails CLOSED (500).  Observe the token -- it fires at connectionTimeoutMS.
Task<(int, string, byte[])?> Gate(HttpListenerContext ctx, CancellationToken token)
{
    if (ctx.Request.Headers["Origin"] != "https://mygame.example")
        return Task.FromResult<(int, string, byte[])?>((403, "text/plain", Encoding.UTF8.GetBytes("forbidden")));
    return Task.FromResult<(int, string, byte[])?>(null);
}
```

### `RGWebSocket` -- the per-connection object you get in every callback

`Send(PooledArray)` / `Send(string)` (thread-safe, queued), `Close()` / `Close(EDisconnectReason)`,
`DisplayId`, `DisconnectReason`, `LastError`, `State`, `UnsentBytes` (queue depth = slow-consumer signal),
`SentMessages/SentBytes/RecvMessages/RecvBytes`, `ConnectedAtTicks`, `RemoteCloseStatus/Description`.
Don't call `Shutdown()` yourself on server sockets -- the server's reaper owns disposal.

---

## 3. HTTP endpoints (server package)

Same port as the websockets. All policies are explicit, required arguments:

```csharp
server.RegisterExactEndpoint ("/metrics", Handler, cacheSeconds: 0, cacheIgnoresQuery: false, authorizer: null);
server.RegisterPrefixEndpoint("/static/", Files,   cacheSeconds: 60, cacheIgnoresQuery: true,  authorizer: null);
server.UnregisterExactEndpoint("/metrics");  server.UnregisterPrefixEndpoint("/static/");

// Handler: return (status, contentType, body).  The token fires at connectionTimeoutMS -- if it does,
// the server has ALREADY answered 503 and anything you return is discarded, so stop working.
async Task<(int, string, byte[])> Handler(HttpListenerContext ctx, CancellationToken token)
    => (200, "text/plain", Encoding.UTF8.GetBytes("ok"));
```

- Routing: exact match wins, else the **longest** matching prefix (registration order never matters).
- `cacheSeconds > 0`: successful (200) GET responses are cached that long -- herd protection for expensive
  public endpoints. Bounded: 100KB/entry max, 32MB + 10k entries total; over budget = served uncached.
- `cacheIgnoresQuery: true` collapses all query strings to one cache entry (use for files/static pages,
  otherwise every unique `?q=` mints an entry an attacker can spam).
- **Never cache an endpoint whose response depends on who's asking.** The authorizer runs on EVERY request
  including cache hits (that's why it exists as a parameter), but every admitted caller gets the same bytes.
- Authorizer: same shape as the upgrade gate -- `null` admits, tuple denies (denials are never cached), throw = 500.

---

## 4. Typed messages: `IRGMessage` + `IMessageFactory`

Wire format: binary frames of `[int32 LE typeId][payload]`. The codec is a swappable strategy -- same
message classes, different factory for JSON-in-dev vs packed-binary-in-prod.

```csharp
public class MoveMsg : IRGMessage { public const int kId = 1; public int TypeId => kId; public int X; }

public class MyFactory : IMessageFactory
{
    public void Serialize(IRGMessage msg, IBufferWriter<byte> writer) { /* write PAYLOAD only; library writes the id */ }
    public IRGMessage? Deserialize(int typeId, ReadOnlySpan<byte> payload)
        => typeId switch { MoveMsg.kId => ParseMove(payload), _ => null };  // null/throw = protocol violation, connection dies
}
```

`Deserialize` gets a `ReadOnlySpan` over a pooled buffer -- the compiler guarantees it can't escape; copy
what you keep. It runs on each socket's receive task, so parsing parallelizes across connections.
Any `IBufferWriter<byte>` serializer works (System.Text.Json, MessagePack, protobuf).

---

## 5. Observability

`server.Metrics` (type `WebSocketServerMetrics`) is live and lock-free, fed automatically:
`CurrentConnections`, `HighWaterConnections`, `TotalAccepted`, `RefusedUpgrades` (cap), `DeniedUpgrades`
(authorizer), `HttpHandlerTimeouts`, `CollectorFaults`, `GetDisconnectCount(reason)`, and distribution
histograms (`InboundMsgBytes`, per-socket lifetime `SentMsgs/RecvMsgs/SentBytes/RecvBytes`,
`ConnectionDurationMS` -- each with `Count/Min/Max/Mean/Percentile(q)`). `Metrics.Report()` returns a
human-readable summary -- wire it to an endpoint and you have a health page.

Pass an `IDataCollection` (namespace `DataCollection`, your prometheus bridge) to the server constructor
and the same numbers are pushed as `rgws_*` gauges/counters/histograms. A sink that throws is contained
and counted (`CollectorFaults`), never allowed to damage a connection.

### `EDisconnectReason` -- every disconnect is attributed, first cause wins

| Value | Meaning |
|---|---|
| `RemoteClose` | peer initiated the close handshake |
| `LocalClose` | this side called `Close()` |
| `TransportError` | network death: reset, abort, vanish mid-handshake |
| `OutboundBackpressure` | peer too slow to consume; `maxUnsentBytes` tripped |
| `InboundOversize` | one message exceeded `maxInboundMessageBytes` |
| `IdleTimeout` | server idle sweep; nothing received for `idleDisconnectSeconds` |
| `UserCodeException` | your callback threw (your bug, honestly labeled) |
| `LocalShutdown` | `Shutdown()`/server stop while the socket was healthy |
| `ProtocolError` | peer spoke the wrong protocol (typed-mode violations) |
| `InboundBacklog` | client app stopped draining `ReceiveAll`; `maxRecvQueueBytes` tripped |

---

## 6. `PooledArray` -- only if you use raw mode or `Send(PooledArray)`

Refcounted buffer from a process-wide pool. The rules:

- `PooledArray.BorrowFromPool(n)` gives you a buffer with `data.Length >= n` (power-of-two bucket,
  128 min, 1GB max) and `Length == n`. You hold one reference.
- `using (buf) { ... }` releases your reference. Refcount 0 = back to the pool. **Everything else follows
  from this**: `Send()` takes its own reference (so `using` your send buffer immediately is correct);
  raw-mode `ReceiveAll` hands you messages you must dispose; a raw-mode `OnRawMessage` buffer is released
  when you return, `IncRef()` if you keep it.
- Double-dispose throws immediately at the offender. Never touch `data` after your release.
- `PooledArray.GetLiveAllocs()` / `GetLiveAllocSize()` count borrowed buffers -- a steadily rising number
  is a leak in YOUR disposal, and a fine assertion in your own shutdown tests.

Also included (namespace `Shared` / `ReachableGames.RGWebSocket`): `ThreadSafeDictionary`,
`ThreadSafeHashSet` (both RW-locked, with atomic `GetOrAdd` / `TryAddBelow`), `LockingList`,
`ChannelQueue` -- the primitives the library itself is built on, usable if you want them.
