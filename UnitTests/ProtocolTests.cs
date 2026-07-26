//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// The server's typed-pipeline strictness matrix and the pre-upgrade authorizer.  Every way a hostile peer can
// violate the typed protocol must land under ProtocolError -- never UserCodeException, which would make an
// attacker's garbage show up in the metrics as OUR bug.  And upgrade denials must be plain HTTP refusals that
// never cost a socket, with a throwing authorizer failing CLOSED.

using Logging;
using System;
using System.Buffers.Binary;
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
			// Factory with a known-good id (7 -> BlobMsg), a known-THROWING id (8), and rejection for everything else.
			public class PickyFactory : IMessageFactory
			{
				public void Serialize(IRGMessage msg, System.Buffers.IBufferWriter<byte> writer)
				{
					System.Buffers.BuffersExtensions.Write(writer, ((BlobMsg)msg).Payload);
				}

				public IRGMessage Deserialize(int typeId, ReadOnlySpan<byte> payload)
				{
					if (typeId == BlobMsg.kTypeId)
						return new BlobMsg() { Payload = payload.ToArray() };
					if (typeId == 8)
						throw new FormatException("deliberate codec explosion on typeId 8");
					return null;
				}
			}

			// Typed manager that just counts what gets through.
			public class StrictManager : RGConnectionManager
			{
				public int MessagesSeen = 0;
				public StrictManager(IMessageFactory factory, ILogging logger) : base(factory, logger) { }
				public override Task OnConnection(RGWebSocket rgws, HttpListenerContext context)       { return Task.CompletedTask; }
				public override Task OnDisconnect(RGWebSocket rgws)                                    { return Task.CompletedTask; }
				public override Task OnMessage   (RGWebSocket rgws, IRGMessage msg)                    { Interlocked.Increment(ref MessagesSeen); return Task.CompletedTask; }
				public override Task Shutdown    () { return Task.CompletedTask; }
			}

			static public class ProtocolTests
			{
				private const int kStrictPort = 9766;
				private const int kAuthPort   = 9767;

				// Open a raw websocket, send one crafted frame, and wait for the server's ProtocolError count to reach
				// the expected value -- proof the violation was noticed AND attributed correctly.
				static private async Task SendRawFrame(RGWebServer server, byte[] frame, bool asText, long expectProtocolErrors, string what)
				{
					using (ClientWebSocket ws = new ClientWebSocket())
					{
						await ws.ConnectAsync(new Uri($"ws://localhost:{kStrictPort}/"), CancellationToken.None).ConfigureAwait(false);
						await ws.SendAsync(new ArraySegment<byte>(frame), asText ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false);
						// Be a COMPLIANT peer about the close: the server disconnects violators gracefully, and if nobody
						// answers its close frame the teardown (correctly) waits out its bounded 5s window -- which would
						// make this test measure the timeout, not the attribution.
						try
						{
							byte[] scratch = new byte[1024];
							using (CancellationTokenSource readTimeout = new CancellationTokenSource(5000))
							{
								while (true)
								{
									WebSocketReceiveResult r = await ws.ReceiveAsync(new ArraySegment<byte>(scratch), readTimeout.Token).ConfigureAwait(false);
									if (r.MessageType == WebSocketMessageType.Close)
									{
										await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", readTimeout.Token).ConfigureAwait(false);
										break;
									}
								}
							}
						}
						catch (Exception) // an abort mid-handshake also ends the connection; the metric below is the real assertion
						{
						}
						await Expect.Within(5000, () => server.Metrics.GetDisconnectCount(EDisconnectReason.ProtocolError) >= expectProtocolErrors, what).ConfigureAwait(false);
					}
				}

				static public async Task Run(TestLogger logger)
				{
					//-------------------
					StrictManager mgr    = new StrictManager(new PickyFactory(), logger);
					RGWebServer   server = new RGWebServer($"http://localhost:{kStrictPort}/", 2, 5000, 30, mgr, logger, null, null);
					server.Start();
					try
					{
						await Runner.Group("typed pipeline strictness (server)",
							("text frame on a typed connection -> ProtocolError", async () =>
							{
								await SendRawFrame(server, System.Text.Encoding.UTF8.GetBytes("hello?"), asText: true, expectProtocolErrors: 1, "text frame disconnects").ConfigureAwait(false);
							}
						),
							("runt frame (under 4 bytes) -> ProtocolError", async () =>
							{
								await SendRawFrame(server, new byte[] { 0xDE, 0xAD }, asText: false, expectProtocolErrors: 2, "runt frame disconnects").ConfigureAwait(false);
							}
						),
							("unknown type id -> ProtocolError", async () =>
							{
								byte[] frame = new byte[8];
								BinaryPrimitives.WriteInt32LittleEndian(frame, 999);
								await SendRawFrame(server, frame, asText: false, expectProtocolErrors: 3, "unknown type id disconnects").ConfigureAwait(false);
							}
						),
							("a THROWING codec -> ProtocolError, never UserCodeException", async () =>
							{
								byte[] frame = new byte[8];
								BinaryPrimitives.WriteInt32LittleEndian(frame, 8); // PickyFactory throws on 8
								await SendRawFrame(server, frame, asText: false, expectProtocolErrors: 4, "codec throw is the peer's garbage, not our bug").ConfigureAwait(false);
								Expect.Eq(0L, server.Metrics.GetDisconnectCount(EDisconnectReason.UserCodeException), "hostile garbage must never be attributed as a user-code fault");
							}
						),
							("a valid typed frame still gets through after all that", async () =>
							{
								byte[] frame = new byte[7];
								BinaryPrimitives.WriteInt32LittleEndian(frame, BlobMsg.kTypeId);
								frame[4] = 1;
								frame[5] = 2;
								frame[6] = 3;
								using (ClientWebSocket ws = new ClientWebSocket())
								{
									await ws.ConnectAsync(new Uri($"ws://localhost:{kStrictPort}/"), CancellationToken.None).ConfigureAwait(false);
									await ws.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false);
									await Expect.Within(5000, () => Volatile.Read(ref mgr.MessagesSeen) == 1, "valid message dispatched to OnMessage").ConfigureAwait(false);
									ws.Abort();
								}
							}
						)).ConfigureAwait(false);
					}
					finally
					{
						await server.Shutdown().ConfigureAwait(false);
					}

					//-------------------
					// Pre-upgrade authorizer: denial is a plain HTTP refusal before the handshake, throwing fails closed,
					// and both are counted as denied upgrades.  A clean client is admitted through the same gate.
					RGWebSocketServer.UpgradeAuthorizer gate = (ctx, token) =>
					{
						if (ctx.Request.Headers["X-Deny"] == "1")
							return Task.FromResult<(int, string, byte[])?>((403, "text/plain", System.Text.Encoding.UTF8.GetBytes("403 Forbidden")));
						if (ctx.Request.Headers["X-Throw"] == "1")
							throw new InvalidOperationException("auth backend down");
						return Task.FromResult<(int, string, byte[])?>(null);
					};
					EchoManager echoMgr    = new EchoManager(logger);
					RGWebServer authServer = new RGWebServer($"http://localhost:{kAuthPort}/", 2, 5000, 30, echoMgr, logger, null, gate);
					authServer.Start();
					try
					{
						await Runner.Group("pre-upgrade authorizer",
							("clean client admitted through the gate", async () =>
							{
								RGUnityWebSocket client = new RGUnityWebSocket(logger, "[admitted]", null, 5000);
								await client.Connect($"ws://localhost:{kAuthPort}/", new Dictionary<string, string>()).ConfigureAwait(false);
								Expect.True(client.IsConnected, $"admitted ({client.LastError})");
								await client.ShutdownAsync().ConfigureAwait(false);
							}
						),
							("denied upgrade never becomes a socket", async () =>
							{
								RGUnityWebSocket client = new RGUnityWebSocket(logger, "[denied]", null, 5000);
								await client.Connect($"ws://localhost:{kAuthPort}/", new Dictionary<string, string>() { { "X-Deny", "1" } }).ConfigureAwait(false);
								Expect.True(client.IsDisconnected, "denial surfaces as a failed connect");
								Expect.Eq(1L, authServer.Metrics.DeniedUpgrades, "counted as denied");
								Expect.Eq(1L, authServer.Metrics.TotalAccepted, "only the earlier admitted client was ever accepted");
								await Expect.Within(2000, () => echoMgr.ConnectedCount == 0, "the manager never saw a connection").ConfigureAwait(false);
							}
						),
							("a throwing authorizer fails CLOSED", async () =>
							{
								RGUnityWebSocket client = new RGUnityWebSocket(logger, "[unlucky]", null, 5000);
								await client.Connect($"ws://localhost:{kAuthPort}/", new Dictionary<string, string>() { { "X-Throw", "1" } }).ConfigureAwait(false);
								Expect.True(client.IsDisconnected, "authorizer fault admits nobody");
								Expect.Eq(2L, authServer.Metrics.DeniedUpgrades, "the fault is counted as a denial too");
							}
						)).ConfigureAwait(false);
					}
					finally
					{
						await authServer.Shutdown().ConfigureAwait(false);
					}
				}
			}
		}
	}
}