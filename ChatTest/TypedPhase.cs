//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// Proof of the typed message layer.  The SAME message classes (TPing/TPong/TChat) run through two completely different
// codecs -- a packed binary factory and a JSON factory -- which is the whole point of IMessageFactory owning both
// Serialize and Deserialize: the codec is swappable without touching a single message class.
// Also proves the strict protocol policy: a client that sends garbage gets disconnected with ProtocolError.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Logging;
using Shared;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		namespace ChatTest
		{
			//-------------------
			// The message vocabulary.  Note: no serialization code in here at all -- these classes work with ANY factory.
			public class TPing : IRGMessage { public const int kTypeId = 1; public int TypeId => kTypeId; public int Seq; }
			public class TPong : IRGMessage { public const int kTypeId = 2; public int TypeId => kTypeId; public int Seq; }
			public class TChat : IRGMessage { public const int kTypeId = 3; public int TypeId => kTypeId; public string Text = string.Empty; }

			//-------------------
			// Production-style codec: hand-packed little-endian binary, minimal bytes, no allocations beyond the message object.
			public class PackedBinaryFactory : IMessageFactory
			{
				public void Serialize(IRGMessage msg, IBufferWriter<byte> writer)
				{
					switch (msg.TypeId)
					{
						case TPing.kTypeId: WriteInt(writer, ((TPing)msg).Seq); break;
						case TPong.kTypeId: WriteInt(writer, ((TPong)msg).Seq); break;
						case TChat.kTypeId: writer.Write(System.Text.Encoding.UTF8.GetBytes(((TChat)msg).Text)); break;
						default: throw new InvalidOperationException($"PackedBinaryFactory cannot serialize unknown TypeId {msg.TypeId}");
					}
				}

				public IRGMessage Deserialize(int typeId, ReadOnlySpan<byte> payload)
				{
					switch (typeId)
					{
						case TPing.kTypeId: return payload.Length==4 ? new TPing() { Seq = BinaryPrimitives.ReadInt32LittleEndian(payload) } : null;
						case TPong.kTypeId: return payload.Length==4 ? new TPong() { Seq = BinaryPrimitives.ReadInt32LittleEndian(payload) } : null;
						case TChat.kTypeId: return new TChat() { Text = System.Text.Encoding.UTF8.GetString(payload) };
						default: return null;  // unknown type id -> ProtocolError disconnect
					}
				}

				static private void WriteInt(IBufferWriter<byte> writer, int value)
				{
					BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(4), value);
					writer.Advance(4);
				}
			}

			//-------------------
			// Development-style codec: sloppy JSON, human-readable on the wire, zero hand-packing effort.
			public class JsonFactory : IMessageFactory
			{
				static private readonly JsonSerializerOptions kOptions = new JsonSerializerOptions() { IncludeFields = true };

				public void Serialize(IRGMessage msg, IBufferWriter<byte> writer)
				{
					writer.Write(JsonSerializer.SerializeToUtf8Bytes(msg, msg.GetType(), kOptions));
				}

				public IRGMessage Deserialize(int typeId, ReadOnlySpan<byte> payload)
				{
					try
					{
						switch (typeId)
						{
							case TPing.kTypeId: return JsonSerializer.Deserialize<TPing>(payload, kOptions);
							case TPong.kTypeId: return JsonSerializer.Deserialize<TPong>(payload, kOptions);
							case TChat.kTypeId: return JsonSerializer.Deserialize<TChat>(payload, kOptions);
							default: return null;
						}
					}
					catch (JsonException)  // malformed payload -> ProtocolError disconnect
					{
						return null;
					}
				}
			}

			//-------------------
			// A tiny typed server: pongs every ping, counts chats.  Note what is ABSENT: no PooledArray, no refcounts, no
			// wire format -- just typed objects in and out.
			public class TypedEchoServer : RGConnectionManager
			{
				private ThreadSafeHashSet<RGWebSocket> _sockets = new ThreadSafeHashSet<RGWebSocket>();
				public int PingsSeen = 0;    // interlocked; OnMessage runs concurrently across sockets
				public int ChatsSeen = 0;

				public TypedEchoServer(IMessageFactory factory, ILogging logger) : base(factory, logger) { }

				public override Task OnConnection(RGWebSocket rgws, System.Net.HttpListenerContext context)
				{
					_sockets.Add(rgws);
					return Task.CompletedTask;
				}

				public override Task OnDisconnect(RGWebSocket rgws)
				{
					_sockets.Remove(rgws);
					return Task.CompletedTask;
				}

				public override Task OnMessage(RGWebSocket rgws, IRGMessage msg)
				{
					switch (msg)
					{
						case TPing ping:
							Interlocked.Increment(ref PingsSeen);
							Send(rgws, new TPong() { Seq = ping.Seq });
							break;
						case TChat:
							Interlocked.Increment(ref ChatsSeen);
							break;
					}
					return Task.CompletedTask;
				}

				public override Task Shutdown()
				{
					_sockets.Foreach((rgws) => rgws.Close());
					return Task.CompletedTask;
				}
			}

			//-------------------
			// Runs one full typed session against a fresh server: N clients ping/chat, all pongs must arrive with correct
			// sequence numbers, and (optionally) a garbage-spewing client must be disconnected with ProtocolError.
			static public class TypedPhase
			{
				private const int kClients = 3;
				private const int kPingsPerClient = 20;

				static public async Task<(bool ok, long protocolErrors, string detail)> Run(int port, IMessageFactory factory, bool includeMalformed, ILogging logger)
				{
					TypedEchoServer mgr = new TypedEchoServer(factory, logger);
					RGWebServer server = new RGWebServer($"http://localhost:{port}/", 2, 5000, 30, mgr, logger, null);
					server.Start();
					try
					{
						// Spin up typed clients, each sends one chat + kPingsPerClient pings.
						RGUnityWebSocket[] clients = new RGUnityWebSocket[kClients];
						HashSet<int>[] pongsSeen = new HashSet<int>[kClients];
						for (int c=0; c<kClients; c++)
						{
							clients[c] = new RGUnityWebSocket(logger, $"[typed{c}]", null, 5000, factory);
							pongsSeen[c] = new HashSet<int>();
							await clients[c].Connect($"ws://localhost:{port}/", new Dictionary<string, string>()).ConfigureAwait(false);
							if (clients[c].IsConnected==false)
								return (false, 0, $"client {c} failed to connect: {clients[c].LastError}");
							clients[c].Send(new TChat() { Text = $"hello from client {c}" });
							for (int i=0; i<kPingsPerClient; i++)
								clients[c].Send(new TPing() { Seq = i });
						}

						// Poll for pongs, exactly like a game loop would.
						List<IRGMessage> inbox = new List<IRGMessage>();
						long deadline = Environment.TickCount64 + 5000;
						int totalPongs = 0;
						while (totalPongs < kClients*kPingsPerClient && Environment.TickCount64 < deadline)
						{
							for (int c=0; c<kClients; c++)
							{
								clients[c].ReceiveAll(inbox);
								foreach (IRGMessage m in inbox)
								{
									if (m is TPong pong && pongsSeen[c].Add(pong.Seq))
										totalPongs++;
								}
								inbox.Clear();
							}
							await Task.Delay(10).ConfigureAwait(false);
						}

						// Optionally, prove that garbage gets you disconnected with the right cause.
						if (includeMalformed)
						{
							using (ClientWebSocket garbage = new ClientWebSocket())
							{
								await garbage.ConnectAsync(new Uri($"ws://localhost:{port}/"), CancellationToken.None).ConfigureAwait(false);
								await garbage.SendAsync(new ArraySegment<byte>(new byte[] { 0xDE, 0xAD }), WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false);  // runt frame
								long gDeadline = Environment.TickCount64 + 5000;
								while (server.Metrics.GetDisconnectCount(EDisconnectReason.ProtocolError)<1 && Environment.TickCount64<gDeadline)
									await Task.Delay(25).ConfigureAwait(false);
							}
						}

						foreach (RGUnityWebSocket client in clients)
						{
							client.Close();
							await Task.Delay(100).ConfigureAwait(false);
							await client.ShutdownAsync().ConfigureAwait(false);
						}

						bool ok = (totalPongs==kClients*kPingsPerClient) && (mgr.PingsSeen==kClients*kPingsPerClient) && (mgr.ChatsSeen==kClients);
						string detail = $"pongs={totalPongs}/{kClients*kPingsPerClient} serverPings={mgr.PingsSeen} serverChats={mgr.ChatsSeen}";
						return (ok, server.Metrics.GetDisconnectCount(EDisconnectReason.ProtocolError), detail);
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
