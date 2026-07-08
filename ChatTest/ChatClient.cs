//-------------------
// Reachable Games
// Copyright 2026
//-------------------
// A standalone chat client for the stress test.  Instance as many of these as you like in one process.
// Each client is built on UnityWebSocket (poll-style, like a game would use), identifies itself with a Guid,
// then "plays around" for a few seconds: /list, broadcasts, whispers to random peers, and binary blobs.
// At the end of a session it either closes gracefully, dies abruptly (no close handshake, simulating a crash),
// or closes and reconnects for another session.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReachableGames
{
	namespace RGWebSocket
	{
		namespace ChatTest
		{
			public class ChatClient
			{
				private const byte kBinaryMagic = 0xAB;  // first byte of every binary payload, so receivers can sanity-check

				public readonly Guid Id = Guid.NewGuid();
				private readonly string        _shortId;
				private readonly string        _url;
				private readonly Random        _rng;
				private readonly OnLogDelegate _logger;
				private readonly int           _startDelayMs;
				private readonly int           _playMs;

				private UnityWebSocket _uws;
				private List<Guid>     _peers = new List<Guid>();  // whoever the last /list response said is present (excluding us)
				private int            _seq   = 0;                 // sequence number so every message is unique

				// Results -- only read these after Run() completes.
				public int    Sessions, ConnectFailures, Welcomes, ListsReceived, ChatsReceived, BinariesReceived, BinaryCorrupt, ServerErrors, UnknownMsgs;
				public int    ListsSent, BroadcastsSent, WhispersSent, BinariesSent;
				public int    GracefulCloses, AbruptDeaths, Reconnects, CloseTimeouts;
				public int    DisconnectCallbacks;  // incremented on the socket's send thread
				public long   BinaryBytesReceived;
				public string FatalError = null;

				public ChatClient(string url, Random rng, OnLogDelegate logger, int startDelayMs, int playMs)
				{
					_url = url;
					_rng = rng;
					_logger = logger;
					_startDelayMs = startDelayMs;
					_playMs = playMs;
					_shortId = Id.ToString().Substring(0, 8);
				}

				public async Task Run()
				{
					try
					{
						await Task.Delay(_startDelayMs).ConfigureAwait(false);
						_uws = new UnityWebSocket(_logger, $"[client {_shortId}]", (uws) => Interlocked.Increment(ref DisconnectCallbacks), 5000);

						int sessionCount = 1 + (_rng.NextDouble()<0.35 ? 1 : 0);  // 35% of clients reconnect for a second session
						for (int s=0; s<sessionCount; s++)
						{
							bool lastSession = (s==sessionCount-1);
							bool graceful = !lastSession || _rng.NextDouble()<0.6;  // non-final sessions close politely so Reconnect() works; final sessions die abruptly 40% of the time
							if (s>0)
								Reconnects++;
							if (await DoSession(s==0, graceful).ConfigureAwait(false)==false)
								break;
						}
					}
					catch (Exception e)
					{
						FatalError = e.ToString();
						_logger(ELogVerboseType.Error, $"[client {_shortId}] FATAL {e}");
					}
					finally
					{
						try
						{
							_uws?.Shutdown();
							if (_uws!=null)  // one final drain so any pooled binary buffers that arrived during teardown get released
							{
								List<UnityWebSocket.wsMessage> leftovers = new List<UnityWebSocket.wsMessage>();
								Drain(leftovers);
							}
						}
						catch (Exception e)
						{
							if (FatalError==null)
								FatalError = e.ToString();
						}
					}
				}

				private async Task<bool> DoSession(bool firstSession, bool graceful)
				{
					if (firstSession)
						await _uws.Connect(_url, new Dictionary<string, string>()).ConfigureAwait(false);
					else
						await _uws.Reconnect().ConfigureAwait(false);  // exercises the cached-url reconnect path
					if (_uws.IsConnected==false)
					{
						ConnectFailures++;
						_logger(ELogVerboseType.Warning, $"[client {_shortId}] connect failed: {_uws.LastError}");
						return false;
					}

					Sessions++;
					_uws.Send($"iam {Id}");
					_uws.Send("/list");
					ListsSent++;

					// Play around for a few seconds: poll like a game loop, do random chat things.
					List<UnityWebSocket.wsMessage> inbox = new List<UnityWebSocket.wsMessage>();
					long endAt = Environment.TickCount64 + _playMs;
					while (Environment.TickCount64<endAt && _uws.IsConnected)
					{
						Drain(inbox);
						DoRandomAction();
						await Task.Delay(_rng.Next(25, 75)).ConfigureAwait(false);
					}

					if (graceful && _uws.IsConnected)
					{
						GracefulCloses++;
						_uws.Close();  // polite close handshake
						long closeDeadline = Environment.TickCount64 + 3000;
						while (_uws.IsDisconnected==false && Environment.TickCount64<closeDeadline)
						{
							Drain(inbox);  // keep draining so nothing pools up while the close completes
							await Task.Delay(20).ConfigureAwait(false);
						}
						if (_uws.IsDisconnected==false)
						{
							CloseTimeouts++;
							_logger(ELogVerboseType.Warning, $"[client {_shortId}] graceful close did not complete within 3s, aborting");
						}
					}
					else if (_uws.IsConnected)
					{
						AbruptDeaths++;  // simulate a crash: no close frame, just rip the socket down below
					}

					_uws.Shutdown();  // hard-stop whatever remains and reset so Reconnect() is legal
					Drain(inbox);     // free any pooled binaries that arrived during teardown
					_peers.Clear();   // stale after a session ends
					return true;
				}

				private void DoRandomAction()
				{
					double r = _rng.NextDouble();
					if (r<0.15)
					{
						if (_uws.Send("/list"))
							ListsSent++;
					}
					else if (r<0.45)
					{
						if (_uws.Send($"/send shout #{_seq++} from {_shortId}"))
							BroadcastsSent++;
					}
					else if (r<0.70)
					{
						Guid peer = PickPeer();
						if (peer!=Guid.Empty && _uws.Send($"/send {peer} whisper #{_seq++} from {_shortId}"))
							WhispersSent++;
					}
					else if (r<0.85)
					{
						int len = _rng.Next(16, 600);
						using (PooledArray pa = PooledArray.BorrowFromPool(len))  // using releases OUR reference; the send queue holds its own
						{
							pa.data[0] = kBinaryMagic;
							for (int i=1; i<len; i++)
								pa.data[i] = (byte)_rng.Next(256);
							if (_uws.Send(pa))
								BinariesSent++;
						}
					}
					// else: idle this tick, like a player thinking
				}

				private Guid PickPeer()
				{
					if (_peers.Count==0)
						return Guid.Empty;
					return _peers[_rng.Next(_peers.Count)];
				}

				// Pull everything out of the socket's incoming queue and process it.  Binary messages are refcounted and MUST be disposed.
				private void Drain(List<UnityWebSocket.wsMessage> inbox)
				{
					_uws.ReceiveAll(inbox);  // appends; we clear below
					for (int i=0; i<inbox.Count; i++)
					{
						if (inbox[i].binMsg!=null)
						{
							using (PooledArray pa = inbox[i].binMsg)  // we own this reference now, release it when done
							{
								BinariesReceived++;
								BinaryBytesReceived += pa.Length;
								if (pa.Length<1 || pa.data[0]!=kBinaryMagic)
									BinaryCorrupt++;
							}
						}
						else
						{
							HandleText(inbox[i].stringMsg);
						}
					}
					inbox.Clear();
				}

				private void HandleText(string msg)
				{
					if (msg=="welcome")
					{
						Welcomes++;
					}
					else if (msg.StartsWith("list", StringComparison.Ordinal))
					{
						ListsReceived++;
						_peers.Clear();
						if (msg.Length>5)
						{
							string[] parts = msg.Substring(5).Split(',', StringSplitOptions.RemoveEmptyEntries);
							for (int i=0; i<parts.Length; i++)
							{
								if (Guid.TryParse(parts[i], out Guid g) && g!=Id)  // don't whisper to ourselves
									_peers.Add(g);
							}
						}
					}
					else if (msg.StartsWith("msg ", StringComparison.Ordinal))
					{
						ChatsReceived++;
					}
					else if (msg.StartsWith("error", StringComparison.Ordinal))
					{
						ServerErrors++;  // includes whispers to members who just left -- a normal race in a chat app
					}
					else
					{
						UnknownMsgs++;
						_logger(ELogVerboseType.Warning, $"[client {_shortId}] unknown message: {msg}");
					}
				}
			}
		}
	}
}
