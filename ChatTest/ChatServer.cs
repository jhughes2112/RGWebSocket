//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// Chat-flavored IConnectionManager used by the stress test.
//
// Protocol (text messages):
//   client -> server:  "iam <guid>"             identify yourself, server replies "welcome"
//   client -> server:  "/list"                  server replies "list <guid>,<guid>,..."
//   client -> server:  "/send <guid> <text>"    server relays "msg <senderGuid> <text>" to that one member
//   client -> server:  "/send <text>"           server relays "msg <senderGuid> <text>" to ALL members (including sender)
//   client -> server:  <binary>                 server relays the binary payload to ALL members (including sender)
//   server -> client:  "error <details>"        anything the server didn't like

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		namespace ChatTest
		{
			public class ChatConnectionManager : IConnectionManager
			{
				private ConcurrentDictionary<RGWebSocket, Guid> _sockets = new ConcurrentDictionary<RGWebSocket, Guid>();  // every live socket, Guid.Empty until it identifies
				private ConcurrentDictionary<Guid, RGWebSocket> _members = new ConcurrentDictionary<Guid, RGWebSocket>();  // only identified members
				private OnLogDelegate _logger;

				// Stats -- all bumped with Interlocked because callbacks arrive on per-socket recv threads.
				private long _connections, _disconnections, _textMsgs, _binaryMsgs, _broadcasts, _whispers, _whisperMisses, _listRequests, _protocolErrors;

				public int  CurrentCount     => _sockets.Count;
				public long Connections      => Interlocked.Read(ref _connections);
				public long Disconnections   => Interlocked.Read(ref _disconnections);

				public ChatConnectionManager(OnLogDelegate logger)
				{
					_logger = logger;
				}

				public Task OnConnection(RGWebSocket rgws, HttpListenerContext context)
				{
					_sockets.TryAdd(rgws, Guid.Empty);
					Interlocked.Increment(ref _connections);
					_logger(ELogVerboseType.Debug, $"ChatServer: connection {rgws._displayId} (now {_sockets.Count})");
					return Task.CompletedTask;
				}

				public Task OnDisconnect(RGWebSocket rgws)
				{
					if (_sockets.TryRemove(rgws, out Guid id) && id!=Guid.Empty)
						_members.TryRemove(id, out _);
					Interlocked.Increment(ref _disconnections);
					_logger(ELogVerboseType.Debug, $"ChatServer: disconnect {rgws._displayId} (now {_sockets.Count})");
					return Task.CompletedTask;
				}

				public Task OnReceiveText(RGWebSocket rgws, string msg)
				{
					Interlocked.Increment(ref _textMsgs);
					if (msg.StartsWith("iam ", StringComparison.Ordinal))
					{
						if (Guid.TryParse(msg.Substring(4), out Guid id))
						{
							_sockets[rgws] = id;
							_members[id] = rgws;
							rgws.Send("welcome");
						}
						else
						{
							Interlocked.Increment(ref _protocolErrors);
							rgws.Send("error bad iam");
						}
					}
					else if (msg=="/list")
					{
						Interlocked.Increment(ref _listRequests);
						rgws.Send("list " + string.Join(",", _members.Keys));
					}
					else if (msg.StartsWith("/send ", StringComparison.Ordinal))
					{
						string payload = msg.Substring(6);
						_sockets.TryGetValue(rgws, out Guid senderId);  // Guid.Empty if they never identified

						// "/send <guid> <text>" is a whisper, "/send <text>" is a broadcast.  Disambiguate by trying to parse the first token as a Guid.
						int space = payload.IndexOf(' ');
						if (space>0 && Guid.TryParse(payload.Substring(0, space), out Guid target))
						{
							Interlocked.Increment(ref _whispers);
							if (_members.TryGetValue(target, out RGWebSocket targetWs))
							{
								targetWs.Send($"msg {senderId} {payload.Substring(space+1)}");
							}
							else  // they may have just disconnected, that's a normal race in a chat app
							{
								Interlocked.Increment(ref _whisperMisses);
								rgws.Send($"error no such member {target}");
							}
						}
						else
						{
							Interlocked.Increment(ref _broadcasts);
							string outMsg = $"msg {senderId} {payload}";
							foreach (var kvp in _members)
								kvp.Value.Send(outMsg);
						}
					}
					else
					{
						Interlocked.Increment(ref _protocolErrors);
						rgws.Send("error unknown command");
					}
					return Task.CompletedTask;
				}

				// Binary messages are relayed to everyone.  RGWebSocket.Send() bumps the refcount per recipient queue,
				// and the recv thread's using-block drops the original reference after we return, so no extra IncRef is needed here.
				public Task OnReceiveBinary(RGWebSocket rgws, PooledArray msg)
				{
					Interlocked.Increment(ref _binaryMsgs);
					foreach (var kvp in _members)
						kvp.Value.Send(msg);
					return Task.CompletedTask;
				}

				// Per the interface contract: close every socket, then wait for the disconnect callbacks to drain them out of our tracking.
				public async Task Shutdown()
				{
					foreach (var kvp in _sockets)
						kvp.Key.Close();
					long deadline = Environment.TickCount64 + 5000;
					while (_sockets.Count>0 && Environment.TickCount64<deadline)
						await Task.Delay(50).ConfigureAwait(false);
					if (_sockets.Count>0)
						_logger(ELogVerboseType.Warning, $"ChatServer.Shutdown: {_sockets.Count} sockets still tracked after 5s of waiting");
				}

				public string StatsString()
				{
					return $"ChatServer stats: connections={_connections} disconnections={_disconnections} stillTracked={_sockets.Count}\n" +
					       $"                  textMsgs={_textMsgs} binaryMsgs={_binaryMsgs} broadcasts={_broadcasts} whispers={_whispers} whisperMisses={_whisperMisses} listRequests={_listRequests} protocolErrors={_protocolErrors}";
				}
			}
		}
	}
}
