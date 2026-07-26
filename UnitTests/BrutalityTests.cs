//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// Brutality suite: this group does not politely exercise the library, it attacks it the way a hostile or broken
// peer would.  Random connect/disconnect churn (graceful closes racing abrupt TCP deaths), a million small
// messages, multi-megabyte messages, and a RAW websocket client that speaks the wire protocol by hand so it can
// send what ClientWebSocket never would: garbage bytes, half a frame followed by death, an oversize message that
// must trip the inbound breaker, and a reader that simply stops reading until the outbound breaker fires.
// Every test ends with the same three questions: did the server return to zero connections, did every disconnect
// get attributed to the RIGHT cause, and did the buffer pool return to its baseline (nothing leaked)?

using Logging;
using Shared;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		namespace UnitTests
		{
			// Echo manager that also counts every disconnect BY CAUSE, so each abuse test can assert its damage was
			// attributed correctly instead of just "some sockets died".
			public class BrutalManager : RGConnectionManager
			{
				private ThreadSafeHashSet<RGWebSocket> _sockets = new ThreadSafeHashSet<RGWebSocket>();
				private int[] _reasons = new int[16]; // indexed by EDisconnectReason; comfortably larger than the enum

				public BrutalManager(ILogging logger) : base(logger) { }

				public int ConnectedCount                        => _sockets.Count;
				public int ReasonCount(EDisconnectReason reason) => Volatile.Read(ref _reasons[(int)reason]);

				public override Task OnConnection(RGWebSocket rgws, HttpListenerContext context) { _sockets.Add(rgws); return Task.CompletedTask; }
				public override Task OnDisconnect(RGWebSocket rgws)                              { _sockets.Remove(rgws); Interlocked.Increment(ref _reasons[(int)rgws.DisconnectReason]); return Task.CompletedTask; }
				public override Task OnMessage   (RGWebSocket rgws, IRGMessage msg)              { return Task.CompletedTask; }
				public override Task Shutdown    () { _sockets.Foreach((rgws) => rgws.Close()); return Task.CompletedTask; }

				public void Broadcast(PooledArray pa) { _sockets.Foreach((rgws) => rgws.Send(pa)); } // Send takes its own reference per socket

				protected override Task OnRawMessage(RGWebSocket rgws, PooledArray msg, bool isText)
				{
					if (isText)
						rgws.Send(Encoding.UTF8.GetString(msg.data, 0, msg.Length));
					else
						rgws.Send(msg);
					return Task.CompletedTask;
				}
			}

			// A websocket client that speaks the wire protocol BY HAND, so tests can send things no real client library
			// will: unmasked frames, reserved opcodes, random garbage, partial frames, and frames it never finishes.
			// It can also complete a perfectly legal handshake and then never read -- the slow-consumer attack.
			public class RawWsClient : IDisposable
			{
				private TcpClient     _tcp;
				private NetworkStream _stream;

				private RawWsClient(TcpClient tcp, NetworkStream stream) { _tcp = tcp; _stream = stream; }

				// Complete a real HTTP upgrade handshake and return a connected raw client.
				static public async Task<RawWsClient> Upgrade(int port)
				{
					TcpClient tcp = new TcpClient();
					await tcp.ConnectAsync("localhost", port).ConfigureAwait(false);
					NetworkStream stream  = tcp.GetStream();
					string        key     = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
					byte[]        request = Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: localhost:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n");
					await stream.WriteAsync(request, 0, request.Length).ConfigureAwait(false);

					// Read until the end of the response headers; anything but 101 is a failed upgrade.
					byte[] buffer = new byte[4096];
					int    total  = 0;
					while (true)
					{
						int n = await stream.ReadAsync(buffer, total, buffer.Length - total).ConfigureAwait(false);
						if (n <= 0)
							throw new Expect.TestFailure("raw upgrade: connection closed during handshake");
						total       += n;
						string soFar = Encoding.ASCII.GetString(buffer, 0, total);
						if (soFar.Contains("\r\n\r\n"))
						{
							if (soFar.StartsWith("HTTP/1.1 101", StringComparison.Ordinal) == false)
								throw new Expect.TestFailure($"raw upgrade: expected 101, got: {soFar.Substring(0, Math.Min(soFar.Length, 64))}");
							return new RawWsClient(tcp, stream);
						}
						if (total == buffer.Length)
							throw new Expect.TestFailure("raw upgrade: response headers exceeded 4KB");
					}
				}

				// Build one client->server frame.  Client frames are masked per RFC6455 (pass masked:false to violate that on purpose).
				static public byte[] BuildFrame(int opcode, ReadOnlySpan<byte> payload, bool fin, bool masked = true)
				{
					int    extended  = payload.Length >= 65536 ? 8 : payload.Length >= 126 ? 2 : 0;
					int    maskBytes = masked ? 4 : 0;
					byte[] frame     = new byte[2 + extended + maskBytes + payload.Length];
					frame[0]         = (byte)((fin ? 0x80 : 0x00) | opcode);
					int idx          = 2;
					if (extended == 0)
						frame[1] = (byte)payload.Length;
					else if (extended == 2)
					{
						frame[1] = 126;
						frame[2] = (byte)(payload.Length >> 8);
						frame[3] = (byte)payload.Length;
						idx      = 4;
					}
					else
					{
						frame[1] = 127;
						long len = payload.Length;
						for (int i = 0; i < 8; i++)
							frame[2 + i] = (byte)(len >> (56 - 8 * i));
						idx = 10;
					}
					if (masked)
					{
						frame[1]      |= 0x80;
						frame[idx + 0] = 0x12;
						frame[idx + 1] = 0x34;
						frame[idx + 2] = 0x56;
						frame[idx + 3] = 0x78; // fixed mask: tests want determinism, not security
						for (int i = 0; i < payload.Length; i++)
							frame[idx + 4 + i] = (byte)(payload[i] ^ frame[idx + (i & 3)]);
						idx += 4;
					}
					else
					{
						payload.CopyTo(new Span<byte>(frame, idx, payload.Length));
					}
					return frame;
				}

				// Write raw bytes; returns false if the server hung up on us mid-write (often the EXPECTED outcome here).
				public async Task<bool> WriteRaw(byte[] bytes, int offset, int count)
				{
					try
					{
						await _stream.WriteAsync(bytes, offset, count).ConfigureAwait(false);
						return true;
					}
					catch (Exception) // server reset the connection -- for breaker tests, that's the point
					{
						return false;
					}
				}

				public Task<bool> SendFrame(int opcode, byte[] payload, bool fin, bool masked = true)
				{
					byte[] frame = BuildFrame(opcode, payload, fin, masked);
					return WriteRaw(frame, 0, frame.Length);
				}

				// Abrupt death: no close frame, no shutdown, just gone -- the crashed-process/pulled-cable case.
				public void Kill()
				{
					try
					{ _tcp.Dispose(); }
					catch (Exception) { }
				}

				public void Dispose() { Kill(); }
			}

			static public class BrutalityTests
			{
				private const int kPort = 9791;

				// Deterministic per-message content so verification never has to store payloads: byte i of message (seed) is Pattern(seed, i).
				static private byte Pattern(int seed, int i) { return (byte)(seed * 31 + i * 7 + (i >> 8)); }

				static private void FillPattern(byte[] data, int length, int seed)
				{
					for (int i = 0; i < length; i++)
						data[i] = Pattern(seed, i);
				}

				static private void VerifyPattern(byte[] data, int length, int seed, string what)
				{
					for (int i = 0; i < length; i++)
						if (data[i] != Pattern(seed, i))
							throw new Expect.TestFailure($"{what}: payload corrupted at byte {i} of {length}");
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
					BrutalManager mgr    = new BrutalManager(logger);
					RGWebServer   server = new RGWebServer($"http://localhost:{kPort}/", 4, 5000, 30, mgr, logger, null, null);
					server.Start();
					long poolBaseline = PooledArray.GetLiveAllocs();
					try
					{
						await Runner.Group("brutality: churn, floods, and hostile peers",
							("random connect/disconnect churn: graceful closes racing abrupt TCP deaths", async () =>
							{
								// Interleaved: well-behaved clients doing short echo sessions ending in Close/Shutdown, while raw
								// clients upgrade and then die every wrong way (instant death, death mid-frame, death after garbage).
								const int kCivilians       = 12; // concurrent well-behaved client tasks
								const int kCyclesPerClient = 6;  // connect/echo/close cycles each
								const int kVandals         = 24; // raw sockets that upgrade and die rudely
								Task[] civilians = new Task[kCivilians];
								for (int c = 0; c < kCivilians; c++)
								{
									int who      = c;
									civilians[c] = Task.Run(async () =>
									{
										Random rng = new Random(1000 + who); // seeded: reproducible chaos
										for (int cycle = 0; cycle < kCyclesPerClient; cycle++)
										{
											RGUnityWebSocket client = new RGUnityWebSocket(logger, $"[churn{who}.{cycle}]", null, 5000);
											await client.Connect($"ws://localhost:{kPort}/", new Dictionary<string, string>()).ConfigureAwait(false);
											if (client.IsConnected == false)
												throw new Expect.TestFailure($"churn client {who} cycle {cycle} failed to connect: {client.LastError}");
											int burst = rng.Next(1, 6);
											for (int m = 0; m < burst; m++)
												client.Send($"churn {who}.{cycle}.{m}");
											// Sometimes drain the echoes, sometimes leave them queued for teardown to discard -- both must be leak-free.
											if (rng.Next(2) == 0)
											{
												List<RGUnityWebSocket.wsMessage> got = new List<RGUnityWebSocket.wsMessage>();
												long deadline = Environment.TickCount64 + 3000;
												while (got.Count < burst && Environment.TickCount64 < deadline)
												{
													client.ReceiveAll(got);
													if (got.Count < burst)
														await Task.Delay(2).ConfigureAwait(false);
												}
												DisposeAll(got);
											}
											if (rng.Next(2) == 0)
												client.Close(); // graceful handshake, then reap
											await client.ShutdownAsync().ConfigureAwait(false);
										}
									});
								}
								Task vandals = Task.Run(async () =>
								{
									Random rng = new Random(31337);
									for (int v = 0; v < kVandals; v++)
									{
										RawWsClient raw = await RawWsClient.Upgrade(kPort).ConfigureAwait(false);
										switch (rng.Next(3))
										{
											case 0: // instant death right after the handshake
												break;
											case 1: // death mid-frame: half a frame header, then gone
												await raw.WriteRaw(new byte[] { 0x82, 0xFE, 0x10 }, 0, 3).ConfigureAwait(false);
												break;
											case 2: // a few dozen bytes of pure garbage, then gone
												byte[] garbage = new byte[48];
												rng.NextBytes(garbage);
												await raw.WriteRaw(garbage, 0, garbage.Length).ConfigureAwait(false);
												break;
										}
										raw.Kill();
										if (v % 4 == 0)
											await Task.Delay(rng.Next(1, 10)).ConfigureAwait(false); // stagger, don't strobe
									}
								});
								await Task.WhenAll(civilians).ConfigureAwait(false);
								await vandals.ConfigureAwait(false);

								await Expect.Within(15000, () => mgr.ConnectedCount == 0, "server reaped every socket after the churn").ConfigureAwait(false);
								Expect.True(mgr.ReasonCount(EDisconnectReason.TransportError) > 0, "abrupt deaths were attributed as TransportError");

								// And the server must still be perfectly healthy.
								RGUnityWebSocket check = new RGUnityWebSocket(logger, "[postchurn]", null, 5000);
								await check.Connect($"ws://localhost:{kPort}/", new Dictionary<string, string>()).ConfigureAwait(false);
								check.Send("still standing?");
								List<RGUnityWebSocket.wsMessage> reply = new List<RGUnityWebSocket.wsMessage>();
								await Expect.Within(5000, () => { check.ReceiveAll(reply); return reply.Count >= 1; }, "server still echoes after the churn").ConfigureAwait(false);
								DisposeAll(reply);
								await check.ShutdownAsync().ConfigureAwait(false);
								await Expect.Within(5000, () => mgr.ConnectedCount == 0, "clean").ConfigureAwait(false);
							}
						),
							("one million small messages, order-verified, across 8 concurrent connections", async () =>
							{
								const int kClients   = 8;
								const int kPerClient = 125_000; // x8 = 1,000,000 messages echoed (2,000,000 over the wire)
								const int kWindow    = 2_000;   // max unacked in flight per client, so no breaker trips on OUR side of the test
								long   startMs = Environment.TickCount64;
								Task[] runners = new Task[kClients];
								for (int c = 0; c < kClients; c++)
								{
									int who    = c;
									runners[c] = Task.Run(async () =>
									{
										RGUnityWebSocket client = new RGUnityWebSocket(logger, $"[mill{who}]", null, 5000);
										await client.Connect($"ws://localhost:{kPort}/", new Dictionary<string, string>()).ConfigureAwait(false);
										if (client.IsConnected == false)
											throw new Expect.TestFailure($"million-msg client {who} failed to connect: {client.LastError}");
										int sent = 0, received = 0;
										List<RGUnityWebSocket.wsMessage> got = new List<RGUnityWebSocket.wsMessage>();
										long deadline = Environment.TickCount64 + 180_000;
										while (received < kPerClient)
										{
											if (Environment.TickCount64 > deadline)
												throw new Expect.TestFailure($"million-msg client {who}: only {received}/{kPerClient} after 180s (client state: connected={client.IsConnected} err={client.LastError})");
											while (sent < kPerClient && sent - received < kWindow)
											{
												using (PooledArray msg = PooledArray.BorrowFromPool(8))
												{
													System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(msg.data, 0, 4), sent);
													System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(msg.data, 4, 4),  who);
													client.Send(msg);
												}
												sent++;
											}
											client.ReceiveAll(got);
											if (got.Count == 0)
											{
												await Task.Delay(1).ConfigureAwait(false);
												continue;
											}
											foreach (RGUnityWebSocket.wsMessage m in got)
											{
												using (m.msg)
												{
													if (m.msg.Length != 8)
														throw new Expect.TestFailure($"million-msg client {who}: echo #{received} came back {m.msg.Length} bytes, expected 8");
													int seq = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(m.msg.data, 0, 4));
													int tag = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(m.msg.data, 4, 4));
													if (seq != received || tag != who)
														throw new Expect.TestFailure($"million-msg client {who}: expected seq {received}, got seq {seq} tag {tag} -- ordering or cross-connection corruption");
													received++;
												}
											}
											got.Clear();
										}
										await client.ShutdownAsync().ConfigureAwait(false);
									});
								}
								await Task.WhenAll(runners).ConfigureAwait(false);
								long elapsedMs = Environment.TickCount64 - startMs;
								Console.WriteLine($"        ({kClients * kPerClient:N0} messages echoed in {elapsedMs / 1000.0:F1}s -- {(long)(kClients * kPerClient * 2 / (elapsedMs / 1000.0)):N0} msgs/sec over the wire)");
								await Expect.Within(10000, () => mgr.ConnectedCount == 0, "server reaped all million-msg clients").ConfigureAwait(false);
							}
						),
							("multi-megabyte messages: byte-perfect echoes just under the inbound limit", async () =>
							{
								// Config for this suite: MaxInboundMessageBytes=8MB.  These are ~7.9MB -- the receive path doubles
								// its buffer eleven times per message and both breakers sit just out of reach.  Sequential on
								// purpose: two in flight would legitimately trip the 16MB outbound breaker, and this test is about
								// the biggest legal message, not the breaker.
								const int kBigBytes = 7_900_000;
								RGUnityWebSocket client = new RGUnityWebSocket(logger, "[big]", null, 5000);
								await client.Connect($"ws://localhost:{kPort}/", new Dictionary<string, string>()).ConfigureAwait(false);
								List<RGUnityWebSocket.wsMessage> got = new List<RGUnityWebSocket.wsMessage>();
								for (int round = 0; round < 3; round++)
								{
									using (PooledArray big = PooledArray.BorrowFromPool(kBigBytes))
									{
										FillPattern(big.data, kBigBytes, round);
										client.Send(big);
									}
									await Expect.Within(30000, () => { client.ReceiveAll(got); return got.Count >= 1; }, $"big echo {round} arrived").ConfigureAwait(false);
									Expect.Eq(        1,         got.Count, $"big echo {round}: exactly one message");
									Expect.Eq(kBigBytes, got[0].msg.Length, $"big echo {round}: length intact");
									VerifyPattern(got[0].msg.data, kBigBytes, round, $"big echo {round}");
									DisposeAll(got);
								}
								await client.ShutdownAsync().ConfigureAwait(false);
								await Expect.Within(5000, () => mgr.ConnectedCount == 0, "clean").ConfigureAwait(false);
							}
						),
							("random-size fuzz: 300 random payloads, byte-perfect, in order", async () =>
							{
								const int kMessages = 300;
								Random           rng    = new Random(4242);
								RGUnityWebSocket client = new RGUnityWebSocket(logger, "[fuzz]", null, 5000);
								await client.Connect($"ws://localhost:{kPort}/", new Dictionary<string, string>()).ConfigureAwait(false);
								int[] sizes = new int[kMessages];
								for (int i = 0; i < kMessages; i++)
									sizes[i] = rng.Next(3) == 0 ? rng.Next(0, 64) : rng.Next(0, 128 * 1024); // a third tiny (including zero-length), the rest up to 128KB
								int sent = 0, received = 0;
								List<RGUnityWebSocket.wsMessage> got = new List<RGUnityWebSocket.wsMessage>();
								long deadline = Environment.TickCount64 + 60_000;
								while (received < kMessages && Environment.TickCount64 < deadline)
								{
									while (sent < kMessages && sent - received < 8) // small window bounds the retained-buffer charges
									{
										using (PooledArray msg = PooledArray.BorrowFromPool(sizes[sent]))
										{
											FillPattern(msg.data, sizes[sent], sent);
											client.Send(msg);
										}
										sent++;
									}
									client.ReceiveAll(got);
									foreach (RGUnityWebSocket.wsMessage m in got)
									{
										using (m.msg)
										{
											Expect.Eq(sizes[received], m.msg.Length, $"fuzz echo {received}: length");
											VerifyPattern(m.msg.data, m.msg.Length, received, $"fuzz echo {received}");
											received++;
										}
									}
									got.Clear();
									if (received < kMessages)
										await Task.Delay(1).ConfigureAwait(false);
								}
								Expect.Eq(kMessages, received, "every fuzz message came back");
								await client.ShutdownAsync().ConfigureAwait(false);
								await Expect.Within(5000, () => mgr.ConnectedCount == 0, "clean").ConfigureAwait(false);
							}
						),
							("oversize message trips the inbound breaker: InboundOversize, server unharmed", async () =>
							{
								// 9MB against an 8MB limit, sent as a raw frame so no client-side breaker interferes.  The server
								// must cut the connection DURING accumulation (never dispatching the partial), attribute it
								// correctly, and keep serving everyone else.
								int         before = mgr.ReasonCount(EDisconnectReason.InboundOversize);
								RawWsClient raw    = await RawWsClient.Upgrade(kPort).ConfigureAwait(false);
								await Expect.Within(5000, () => mgr.ConnectedCount == 1, "raw client registered").ConfigureAwait(false);
								byte[] payload = new byte[9 * 1024 * 1024];
								byte[] frame   = RawWsClient.BuildFrame(0x2, payload, fin: true);
								// Chunked writes; the server resets us partway through, and that write failure IS the breaker working.
								for (int offset = 0; offset < frame.Length; offset += 256 * 1024)
								{
									if (await raw.WriteRaw(frame, offset, Math.Min(256 * 1024, frame.Length - offset)).ConfigureAwait(false) == false)
										break;
								}
								await Expect.Within(10000, () => mgr.ReasonCount(EDisconnectReason.InboundOversize) == before + 1, "attributed as InboundOversize").ConfigureAwait(false);
								await Expect.Within(10000, () => mgr.ConnectedCount == 0, "abuser reaped").ConfigureAwait(false);
								raw.Kill();

								RGUnityWebSocket check = new RGUnityWebSocket(logger, "[postoversize]", null, 5000);
								await check.Connect($"ws://localhost:{kPort}/", new Dictionary<string, string>()).ConfigureAwait(false);
								check.Send("survived?");
								List<RGUnityWebSocket.wsMessage> reply = new List<RGUnityWebSocket.wsMessage>();
								await Expect.Within(5000, () => { check.ReceiveAll(reply); return reply.Count >= 1; }, "server still echoes after the oversize attack").ConfigureAwait(false);
								DisposeAll(reply);
								await check.ShutdownAsync().ConfigureAwait(false);
								await Expect.Within(5000, () => mgr.ConnectedCount == 0, "clean").ConfigureAwait(false);
							}
						),
							("slow consumer trips the outbound breaker: OutboundBackpressure, memory bounded", async () =>
							{
								// A raw client completes a legal handshake and then never reads a byte.  The server broadcasts
								// 40MB at it; kernel buffers absorb a sliver, the unsent queue eats the rest until it crosses the
								// 16MB breaker.  The library must cut the socket loose rather than hoard memory for a dead reader.
								int         before = mgr.ReasonCount(EDisconnectReason.OutboundBackpressure);
								RawWsClient raw    = await RawWsClient.Upgrade(kPort).ConfigureAwait(false);
								await Expect.Within(5000, () => mgr.ConnectedCount == 1, "raw client registered").ConfigureAwait(false);
								using (PooledArray chunk = PooledArray.BorrowFromPool(1024 * 1024))
								{
									for (int i = 0; i < 40 && mgr.ConnectedCount > 0; i++)
										mgr.Broadcast(chunk); // each Send charges ~1MB against the 16MB budget; the pump can't drain into a full pipe
								}
								await Expect.Within(15000, () => mgr.ReasonCount(EDisconnectReason.OutboundBackpressure) == before + 1, "attributed as OutboundBackpressure").ConfigureAwait(false);
								await Expect.Within(15000, () => mgr.ConnectedCount == 0, "slow consumer reaped").ConfigureAwait(false);
								raw.Kill();
							}
						),
							("protocol garbage: reserved opcodes, unmasked frames, and noise never crash the server", async () =>
							{
								// Three separate hostile clients, three separate wire-level violations.  Each must cost exactly its
								// own connection -- attributed as a transport-level failure -- and nothing else.
								RawWsClient reserved = await RawWsClient.Upgrade(kPort).ConfigureAwait(false);
								await reserved.SendFrame(0x3, Encoding.ASCII.GetBytes("reserved opcode"), fin: true).ConfigureAwait(false); // opcodes 3-7 are reserved, must be rejected

								RawWsClient unmasked = await RawWsClient.Upgrade(kPort).ConfigureAwait(false);
								await unmasked.SendFrame(0x2, Encoding.ASCII.GetBytes("unmasked client frame"), fin: true, masked: false).ConfigureAwait(false); // client frames MUST be masked; the server must refuse

								RawWsClient noise = await RawWsClient.Upgrade(kPort).ConfigureAwait(false);
								byte[]      junk  = new byte[256];
								new Random(666).NextBytes(junk);
								await noise.WriteRaw(junk, 0, junk.Length).ConfigureAwait(false);

								await Expect.Within(10000, () => mgr.ConnectedCount == 0, "all three violators disconnected").ConfigureAwait(false);
								reserved.Kill();
								unmasked.Kill();
								noise.Kill();

								// The declared-length lie: a frame header promising 100MB that never delivers.  The server must not
								// preallocate for the promise (that would be a one-frame OOM), and killing the socket must clean up.
								RawWsClient liar        = await RawWsClient.Upgrade(kPort).ConfigureAwait(false);
								byte[]      lyingHeader = new byte[] { 0x82, 0xFF, 0, 0, 0, 0, 0x06, 0x40, 0x00, 0x00, 0x12, 0x34, 0x56, 0x78 }; // FIN+binary, masked, declares 104,857,600 bytes, sends none
								await liar.WriteRaw(lyingHeader, 0, lyingHeader.Length).ConfigureAwait(false);
								await Task.Delay(200).ConfigureAwait(false); // give it time to do damage if it were going to
								liar.Kill();
								await Expect.Within(10000, () => mgr.ConnectedCount == 0, "liar reaped after death").ConfigureAwait(false);

								// After every violation, business as usual.
								RGUnityWebSocket check = new RGUnityWebSocket(logger, "[postgarbage]", null, 5000);
								await check.Connect($"ws://localhost:{kPort}/", new Dictionary<string, string>()).ConfigureAwait(false);
								check.Send("unbothered?");
								List<RGUnityWebSocket.wsMessage> reply = new List<RGUnityWebSocket.wsMessage>();
								await Expect.Within(5000, () => { check.ReceiveAll(reply); return reply.Count >= 1; }, "server still echoes after the garbage parade").ConfigureAwait(false);
								DisposeAll(reply);
								await check.ShutdownAsync().ConfigureAwait(false);
								await Expect.Within(5000, () => mgr.ConnectedCount == 0, "clean").ConfigureAwait(false);
							}
						),
							("pool returns to baseline after all of the above (nothing leaked, nothing double-freed)", async () =>
							{
								await Expect.Within(10000, () => PooledArray.GetLiveAllocs() == poolBaseline, $"live pooled buffers back to baseline ({poolBaseline}); currently {PooledArray.GetLiveAllocs()}").ConfigureAwait(false);
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