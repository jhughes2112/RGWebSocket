//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// Websocket behavior against a REAL server: echo roundtrips (text, binary across buffer growth, zero-length
// heartbeats), disconnect-reason attribution on both ends, user-code exception containment, API misuse, and
// the client's connect-failure path.  ChatTest remains the big stress gate; these are the precise, targeted checks.

using Logging;
using Shared;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		namespace UnitTests
		{
			// Raw-mode echo: text comes back as text, binary as binary.  The magic text "throw" throws, to prove
			// user-code exceptions are contained and attributed.  Captures the server-side disconnect reason.
			public class EchoManager : RGConnectionManager
			{
				private ThreadSafeHashSet<RGWebSocket> _sockets = new ThreadSafeHashSet<RGWebSocket>();
				private int _lastReason = (int)EDisconnectReason.None;

				public EDisconnectReason LastDisconnectReason => (EDisconnectReason)Volatile.Read(ref _lastReason);
				public int               ConnectedCount       => _sockets.Count;

				public EchoManager(ILogging logger) : base(logger) { }

				public override Task OnConnection(RGWebSocket rgws, HttpListenerContext context) { _sockets.Add(rgws); return Task.CompletedTask; }
				public override Task OnDisconnect(RGWebSocket rgws)                              { _sockets.Remove(rgws); Volatile.Write(ref _lastReason, (int)rgws.DisconnectReason); return Task.CompletedTask; }
				public override Task OnMessage   (RGWebSocket rgws, IRGMessage msg)              { return Task.CompletedTask; }
				public override Task Shutdown    () { _sockets.Foreach((rgws) => rgws.Close()); return Task.CompletedTask; }

				public void KickAll() { _sockets.Foreach((rgws) => rgws.Close()); }

				protected override Task OnRawMessage(RGWebSocket rgws, PooledArray msg, bool isText) // cross-assembly override of protected internal drops the internal half
				{
					if (isText)
					{
						string text = System.Text.Encoding.UTF8.GetString(msg.data, 0, msg.Length);
						if (text == "throw")
							throw new InvalidOperationException("EchoManager: deliberate user-code exception");
						rgws.Send(text);
					}
					else
					{
						rgws.Send(msg); // Send takes its own reference; ours releases when we return
					}
					return Task.CompletedTask;
				}
			}

			static public class SocketTests
			{
				private const int kEchoPort = 9765;
				private const int kDeadPort = 9769; // nothing listens here

				// Drain until n messages arrive or the deadline passes.  Caller owns (and must dispose) every message.
				static private async Task<List<RGUnityWebSocket.wsMessage>> ReceiveN(RGUnityWebSocket client, int n, int timeoutMs)
				{
					List<RGUnityWebSocket.wsMessage> got = new List<RGUnityWebSocket.wsMessage>();
					long deadline = Environment.TickCount64 + timeoutMs;
					while (got.Count < n && Environment.TickCount64 < deadline)
					{
						client.ReceiveAll(got);
						if (got.Count < n)
							await Task.Delay(10).ConfigureAwait(false);
					}
					return got;
				}

				static private void DisposeAll(List<RGUnityWebSocket.wsMessage> messages)
				{
					foreach (RGUnityWebSocket.wsMessage m in messages)
						using (m.msg)
						{ }
					messages.Clear();
				}

				static public async Task Run(TestLogger logger)
				{
					await Runner.Group("RGWebSocket API misuse",
						("constructor rejects nulls", () =>
						{
							ClientWebSocket ws = new ClientWebSocket();
							Expect.Throws<ArgumentNullException>(        () => new RGWebSocket(null, null, (rgws) => Task.CompletedTask, logger, "x", ws), "null onReceiveMsg");
							Expect.Throws<ArgumentNullException>(() => new RGWebSocket(null, (rgws, msg, t) => Task.CompletedTask, null, logger, "x", ws), "null onDisconnection");
							Expect.Throws<ArgumentNullException>(    () => new RGWebSocket(null, (rgws, msg, t) => Task.CompletedTask, (rgws) => Task.CompletedTask, null, "x", ws), "null logger");
							Expect.Throws<ArgumentNullException>(() => new RGWebSocket(null, (rgws, msg, t) => Task.CompletedTask, (rgws) => Task.CompletedTask, logger, "x", null), "null websocket");
							ws.Dispose();
							return Task.CompletedTask;
						}
					),
						("Start twice throws; pumps on a dead transport self-terminate", async () =>
						{
							RGWebSocket rgws = new RGWebSocket(null, (s, m, t) => Task.CompletedTask, (s) => Task.CompletedTask, logger, "twice", new ClientWebSocket());
							rgws.Start();
							Expect.Throws<InvalidOperationException>(() => rgws.Start(), "Start is once-only");
							// Wait for the pumps to observe the dead transport BEFORE shutting down -- otherwise Shutdown's
							// LocalShutdown legitimately wins the first-stamp race and the assertion tests scheduling, not code.
							await Expect.Within(5000, () => rgws.DisconnectReason != EDisconnectReason.None, "pumps notice the dead transport on their own").ConfigureAwait(false);
							Expect.Eq(EDisconnectReason.TransportError, rgws.DisconnectReason, "an unconnected transport is a transport error, stamped by the pumps");
							await rgws.Shutdown().ConfigureAwait(false);
						}
					),
						("Shutdown before Start is safe and idempotent", async () =>
						{
							RGWebSocket rgws = new RGWebSocket(null, (s, m, t) => Task.CompletedTask, (s) => Task.CompletedTask, logger, "nostart", new ClientWebSocket());
							Task        a    = rgws.Shutdown();
							Task        b    = rgws.Shutdown(); // concurrent second caller awaits the same completion
							await Task.WhenAll(a, b).ConfigureAwait(false);
							Expect.Eq(EDisconnectReason.LocalShutdown, rgws.DisconnectReason, "a healthy socket shut down locally says so");
						}
					),
						("typed API on a raw-mode client throws immediately", () =>
						{
							RGUnityWebSocket raw = new RGUnityWebSocket(logger, "[rawmode]", null, 1000);
							Expect.Throws<InvalidOperationException>(               () => raw.Send(new BlobMsg()), "typed Send needs the factory constructor");
							Expect.Throws<InvalidOperationException>(() => raw.ReceiveAll(new List<IRGMessage>()), "typed ReceiveAll needs the factory constructor");
							return Task.CompletedTask;
						}
					)).ConfigureAwait(false);

					//-------------------
					EchoManager mgr    = new EchoManager(logger);
					RGWebServer server = new RGWebServer($"http://localhost:{kEchoPort}/", 2, 5000, 30, mgr, logger, null, null);
					server.Start();
					try
					{
						await Runner.Group("websocket echo + disconnect reasons",
							("text, binary (across buffer growth), and zero-length heartbeat roundtrips", async () =>
							{
								RGUnityWebSocket client = new RGUnityWebSocket(logger, "[echo]", null, 5000);
								await client.Connect($"ws://localhost:{kEchoPort}/", new Dictionary<string, string>()).ConfigureAwait(false);
								Expect.True(client.IsConnected, $"connected ({client.LastError})");

								// Text.
								client.Send("hello websocket");
								List<RGUnityWebSocket.wsMessage> got = await ReceiveN(client, 1, 5000).ConfigureAwait(false);
								Expect.Eq(                1,     got.Count, "text echo arrived");
								Expect.Eq(             true, got[0].isText, "echoed as a text frame");
								Expect.Eq("hello websocket",   got[0].Text, "text payload intact");
								DisposeAll(got);

								// Binary, larger than the receive buffer so the doubling growth path runs on both ends.
								byte[] pattern = new byte[64 * 1024];
								new Random(7).NextBytes(pattern);
								using (PooledArray blob = PooledArray.BorrowFromPool(pattern.Length))
								{
									Buffer.BlockCopy(pattern, 0, blob.data, 0, pattern.Length);
									client.Send(blob);
								}
								got = await ReceiveN(client, 1, 5000).ConfigureAwait(false);
								Expect.Eq(             1,         got.Count, "binary echo arrived");
								Expect.Eq(         false,     got[0].isText, "echoed as a binary frame");
								Expect.Eq(pattern.Length, got[0].msg.Length, "binary length intact");
								for (int i = 0; i < pattern.Length; i++)
									if (got[0].msg.data[i] != pattern[i])
										throw new Expect.TestFailure($"binary payload corrupted at byte {i}");
								DisposeAll(got);

								// Zero-length binary: a legal heartbeat, and it must survive the whole path.
								using (PooledArray empty = PooledArray.BorrowFromPool(0))
									client.Send(empty);
								got = await ReceiveN(client, 1, 5000).ConfigureAwait(false);
								Expect.Eq(1,         got.Count, "zero-length echo arrived");
								Expect.Eq(0, got[0].msg.Length, "zero-length preserved");
								DisposeAll(got);

								await client.ShutdownAsync().ConfigureAwait(false);
								await Expect.Within(5000, () => mgr.ConnectedCount == 0, "server side reaped").ConfigureAwait(false);
							}
						),
							("message ordering survives the queue and both pumps", async () =>
							{
								RGUnityWebSocket client = new RGUnityWebSocket(logger, "[ordered]", null, 5000);
								await client.Connect($"ws://localhost:{kEchoPort}/", new Dictionary<string, string>()).ConfigureAwait(false);
								const int kMessages = 200;
								for (int i = 0; i < kMessages; i++)
									client.Send($"seq {i}");
								List<RGUnityWebSocket.wsMessage> got = await ReceiveN(client, kMessages, 10000).ConfigureAwait(false);
								Expect.Eq(kMessages, got.Count, "every message echoed");
								for (int i = 0; i < kMessages; i++)
									if (got[i].Text != $"seq {i}")
										throw new Expect.TestFailure($"order broken at {i}: got \"{got[i].Text}\"");
								DisposeAll(got);
								await client.ShutdownAsync().ConfigureAwait(false);
								await Expect.Within(5000, () => mgr.ConnectedCount == 0, "server side reaped").ConfigureAwait(false);
							}
						),
							("client-initiated close: LocalClose here, RemoteClose there", async () =>
							{
								RGUnityWebSocket client = new RGUnityWebSocket(logger, "[closer]", null, 5000);
								await client.Connect($"ws://localhost:{kEchoPort}/", new Dictionary<string, string>()).ConfigureAwait(false);
								Expect.True(client.IsConnected, "connected");
								await Expect.Within(5000, () => mgr.ConnectedCount == 1, "server registered the socket").ConfigureAwait(false);
								client.Close();
								await Expect.Within(5000, () => client.IsDisconnected, "client saw the close complete").ConfigureAwait(false);
								Expect.Eq(EDisconnectReason.LocalClose, client.DisconnectReason, "closer's own reason");
								await Expect.Within(5000, () => mgr.ConnectedCount == 0, "server side disconnected").ConfigureAwait(false);
								Expect.Eq(EDisconnectReason.RemoteClose, mgr.LastDisconnectReason, "server attributes it to the peer");
								await client.ShutdownAsync().ConfigureAwait(false);
							}
						),
							("server-initiated kick: RemoteClose at the client, LocalClose at the server", async () =>
							{
								RGUnityWebSocket client = new RGUnityWebSocket(logger, "[kicked]", null, 5000);
								await client.Connect($"ws://localhost:{kEchoPort}/", new Dictionary<string, string>()).ConfigureAwait(false);
								await Expect.Within(5000, () => mgr.ConnectedCount == 1, "server registered the socket").ConfigureAwait(false);
								mgr.KickAll();
								await Expect.Within(5000, () => client.IsDisconnected, "client saw the server's close").ConfigureAwait(false);
								Expect.Eq(EDisconnectReason.RemoteClose, client.DisconnectReason, "client attributes it to the peer");
								await Expect.Within(5000, () => mgr.ConnectedCount == 0, "server side reaped").ConfigureAwait(false);
								Expect.Eq(EDisconnectReason.LocalClose, mgr.LastDisconnectReason, "server's own reason");
								await client.ShutdownAsync().ConfigureAwait(false);
							}
						),
							("a throwing message handler kills only that connection, attributed UserCodeException", async () =>
							{
								RGUnityWebSocket victim = new RGUnityWebSocket(logger, "[victim]", null, 5000);
								await victim.Connect($"ws://localhost:{kEchoPort}/", new Dictionary<string, string>()).ConfigureAwait(false);
								await Expect.Within(5000, () => mgr.ConnectedCount == 1, "server registered the socket").ConfigureAwait(false);
								victim.Send("throw");
								await Expect.Within(5000, () => mgr.ConnectedCount == 0, "server dropped the connection whose handler threw").ConfigureAwait(false);
								Expect.Eq(EDisconnectReason.UserCodeException, mgr.LastDisconnectReason, "attributed as OUR code's fault, not the peer's");
								await Expect.Within(5000, () => victim.IsDisconnected, "client saw the drop").ConfigureAwait(false);
								await victim.ShutdownAsync().ConfigureAwait(false);

								// The server must still be perfectly healthy for the next client.
								RGUnityWebSocket next = new RGUnityWebSocket(logger, "[next]", null, 5000);
								await next.Connect($"ws://localhost:{kEchoPort}/", new Dictionary<string, string>()).ConfigureAwait(false);
								next.Send("still alive?");
								List<RGUnityWebSocket.wsMessage> got = await ReceiveN(next, 1, 5000).ConfigureAwait(false);
								Expect.Eq(1, got.Count, "server still echoes after another connection's handler threw");
								DisposeAll(got);
								await next.ShutdownAsync().ConfigureAwait(false);
								await Expect.Within(5000, () => mgr.ConnectedCount == 0, "clean").ConfigureAwait(false);
							}
						),
							("connect failure leaves the client reusable", async () =>
							{
								RGUnityWebSocket client = new RGUnityWebSocket(logger, "[noserver]", null, 1500);
								await client.Connect($"ws://localhost:{kDeadPort}/", new Dictionary<string, string>()).ConfigureAwait(false);
								Expect.True(      client.IsDisconnected, "failed connect resets to ReadyToConnect");
								Expect.True(client.LastError.Length > 0, "the failure is recorded in LastError");
								// The same instance can then connect somewhere real.
								await client.Connect($"ws://localhost:{kEchoPort}/", new Dictionary<string, string>()).ConfigureAwait(false);
								Expect.True(client.IsConnected, "same instance connects fine after a failure");
								await client.ShutdownAsync().ConfigureAwait(false);
								await Expect.Within(5000, () => mgr.ConnectedCount == 0, "clean").ConfigureAwait(false);
							}
						)).ConfigureAwait(false);
					}
					finally
					{
						await server.Shutdown().ConfigureAwait(false);
					}
				}
			}
		}
	}
}