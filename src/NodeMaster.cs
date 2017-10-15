using System.Collections.Generic;
using System.Threading;
using System.Text.RegularExpressions;
using System;

namespace Cube
{
	public class NodeMaster
	{
		public const int DEFAULT_NODE_PORT = 8400;
		public const int DEFAULT_CLIENT_PORT = 8401;

		class NodeRecord
		{
			public GameNodeConnection Connection;
			public Netki.GameNodeInfo Info;
			public int PendingCreateRequests;
			public float Lag;
			public string[] AcceptedConfigurations;
		}

		public class RequestEntry
		{
			public Netki.Packet Request, OriginalRequest;
			public GameClientConnection Connection;
			public string RequestId;
			public int TicksWaited;
			public int RetryCounter;
		}

		public class Authorization
		{
			public string Host;
			public int Port;
			public string AuthToken;
			public string KnockToken;
		}

		public class ResponseRedirectionEntry
		{
			public GameClientConnection Where;
			public DateTime Expires;
			public int References;
		}

		PacketStreamServer _node_serv;
		PacketStreamServer _client_serv;
		private ApplicationPacketHandler _app_packet_handler;
		Dictionary<string, NodeRecord> _instances = new Dictionary<string, NodeRecord>();
		Dictionary<string, Authorization> _auth = new Dictionary<string, Authorization>();
		Dictionary<string, ResponseRedirectionEntry> _response_redir = new Dictionary<string, ResponseRedirectionEntry>();
		List<RequestEntry> _requests = new List<RequestEntry>();
		Thread _update_thread;

		// this is a big huge database over what player is at what game, and is used for
		// rejoining
		class PlayerAtNode
		{
			public List<string> nodeIds;
		}

		Dictionary<string, PlayerAtNode> _playerAtNode = new Dictionary<string, PlayerAtNode>();

		public NodeMaster()
		{
			_app_packet_handler = new MasterPacketsHandler();
			_node_serv = new PacketStreamServer(new NodeConnectionHandler(this));
			_client_serv = new PacketStreamServer(new ClientConnectionHandler(this));
			_update_thread = new Thread(UpdateThread);
		}

		static int _token_counter = 0;
		static Random _token_random = new Random();
		static DateTime _token_start = DateTime.Now;

		public ApplicationPacketHandler GetPacketHandler()
		{
			return _app_packet_handler;
		}

		public string MakeToken()
		{
			lock (_token_random)
			{
				using (var sha = System.Security.Cryptography.SHA256.Create())
				{
					string mix = "aschja23" + _token_random.NextDouble() + "-baberiba-" + DateTime.Now + "_" + DateTime.Now.Millisecond + "_" + (_token_counter) + " servertime=" + _token_start;
					var computedHash = sha.ComputeHash(System.Text.Encoding.ASCII.GetBytes(mix));
					var token = Convert.ToBase64String(computedHash);
					Debug.MasterLog("Token from [" + mix + "] made [" + token + "]");
					return token;
				}
			}
		}

		public void Start()
		{
			_node_serv.Start(DEFAULT_NODE_PORT, 100);
			_client_serv.Start(DEFAULT_CLIENT_PORT, 10000);
			_update_thread.Start();
		}

		public void RegisterInstance(Netki.GameNodeInfo info, GameNodeConnection Connection)
		{
			lock (_instances)
			{
				NodeRecord r;
				if (_instances.ContainsKey(info.NodeId))
				{
					r = _instances[info.NodeId];
					_instances.Remove(info.NodeId);
				}
				else
				{
					r = new NodeRecord();
				}

				r.Info = info;
				r.Connection = Connection;
				_instances.Add(info.NodeId, r);
			}
			Debug.MasterLog("Added instance [" + info.NodeId + "]");
		}

		// These will not be more than one per connection.
		public void OnClientRequest(Netki.Packet packet, GameClientConnection conn)
		{
			lock (_requests)
			{
				RequestEntry req = new RequestEntry();
				req.Request = packet;
				req.Connection = conn;
				req.TicksWaited = 0;
				_requests.Add(req);
			}
		}

		public void OnNodePacket(string nodeId, Netki.Packet p)
		{
			switch (p.type_id)
			{
				case Netki.GameNodePing.TYPE_ID:
					{
						Netki.GameNodePing ping = (Netki.GameNodePing)p;
						lock (_instances)
						{
							NodeRecord node;
							if (_instances.TryGetValue(nodeId, out node))
							{
								uint diff = ((uint)DateTime.UtcNow.Ticks) - ping.Time;
								node.Lag = (int)diff / 10000.0f;
							}
						}
						return;
					}
				case Netki.GameNodeConfigurationsSupport.TYPE_ID:
					{
						Netki.GameNodeConfigurationsSupport support = (Netki.GameNodeConfigurationsSupport)p;
						lock (_instances)
						{
							NodeRecord node;
							if (_instances.TryGetValue(nodeId, out node))
								node.AcceptedConfigurations = support.Patterns;
						}
						return;
					}
				case Netki.GameNodePlayerIsOnGames.TYPE_ID:
					{
						Netki.GameNodePlayerIsOnGames pkt = (Netki.GameNodePlayerIsOnGames)p;
						Debug.MasterLog("Response from [" + nodeId + "] player " + pkt.PlayerId + " is on " + pkt.GameIds.Length + " games");

						// clean up junk
						if (pkt.GameIds.Length == 0)
						{
							lock (_playerAtNode)
							{
								if (_playerAtNode.ContainsKey(nodeId))
									_playerAtNode[nodeId].nodeIds.Remove(nodeId);
							}
						}

						lock (_response_redir)
						{
							if (_response_redir.ContainsKey(pkt.RequestId))
							{
								// transform and forward
								Netki.MasterJoinedGamesResponse resp = new Netki.MasterJoinedGamesResponse();
								resp.GameIds = pkt.GameIds;
								ResponseRedirectionEntry entry = _response_redir[pkt.RequestId];
								entry.Where.SendPacket(resp);
								entry.References--;
								if (entry.References == 0)
									_response_redir.Remove(pkt.RequestId);
							}
						}
						return;
					}
				case Netki.GameNodeGamesList.TYPE_ID:
					{
						Netki.GameNodeGamesList list = (Netki.GameNodeGamesList)p;
						string[] configs = null;

						lock (_instances)
						{
							NodeRecord nr;
							if (_instances.TryGetValue(nodeId, out nr))
							{
								nr.Info.Games = list;
								configs = nr.AcceptedConfigurations;
							}
						}

						Debug.MasterLog("Node ping response [" + nodeId + "] (lag:" + _instances[nodeId].Lag + " ms)");
						for (int i = 0; i < list.Games.Length; i++)
						{
							Debug.MasterLog("G" + i + " [" + list.Games[i].Id + "] host=" + list.Games[i].Host + " port=" + list.Games[i].Port + " conf=" + list.Games[i].Configuration + " info=" + list.Games[i].Info + " free=" + list.Games[i].Status.PlayerSlotsLeft + " players=" + list.Games[i].Status.PlayersJoined);
						}
						if (configs != null)
						{
							foreach (string s in configs)
							{
								Debug.MasterLog("    " + nodeId + " accepts configuration [" + s + "]");
							}
						}

						Debug.MasterLog("--------");
						return;
					}
				case Netki.GameNodeCreateGameResponse.TYPE_ID:
					{
						Netki.GameNodeCreateGameResponse resp = (Netki.GameNodeCreateGameResponse)p;
						if (resp.GameId != null)
						{
							Debug.MasterLog("Node [" + nodeId + "] created requested game [" + resp.GameId + "]");
						}
						else
						{
							// should downprioritize this node...
							Debug.MasterLog("Node [" + nodeId + "] failed to create requested game");
						}
						lock (_instances)
						{
							if (_instances.ContainsKey(nodeId))
							{
								_instances[nodeId].PendingCreateRequests--;
								Debug.MasterLog("Node [" + nodeId + "] pendingRequests=" + _instances[nodeId].PendingCreateRequests);
							}
						}
						return;
					}
				case Netki.GameNodeAuthPlayer.TYPE_ID:
					{
						Netki.GameNodeAuthPlayer auth = (Netki.GameNodeAuthPlayer)p;
						Debug.MasterLog("Node [" + nodeId + "] responded to auth success=" + auth.Success);
						lock (_auth)
						{
							if (_auth.ContainsKey(auth.RequestId))
								_auth.Remove(auth.RequestId);

							// store authorization
							if (auth.Success)
							{
								Authorization na = new Authorization();
								na.AuthToken = auth.AuthToken;
								na.KnockToken = auth.KnockToken;
								na.Host = auth.Host;
								na.Port = auth.Port;
								_auth[auth.RequestId] = na;
							}
							else
							{
								_auth[auth.RequestId] = null;
							}
						}

						// Add player to big huge games list.
						lock (_playerAtNode)
						{
							PlayerAtNode entry;
							if (!_playerAtNode.ContainsKey(auth.PlayerId))
							{
								entry = new PlayerAtNode();
								entry.nodeIds = new List<string>();
								_playerAtNode[auth.PlayerId] = entry;
							}
							else
							{
								entry = _playerAtNode[auth.PlayerId];
							}
							if (!entry.nodeIds.Contains(nodeId))
								entry.nodeIds.Add(nodeId);
						}
						return;
					}
			}
		}

		public void DisconnectInstance(string Id)
		{
			lock (_instances)
			{
				if (_instances.ContainsKey(Id))
					_instances[Id].Connection = null;
			}
			Debug.MasterLog("Disconnecting instance " + Id);
		}


		bool SpawnCreateGameRequest(string Configuration)
		{
			lock (_instances)
			{
				List<NodeRecord> candidates = new List<NodeRecord>();
				int bestScore = 0;

				foreach (NodeRecord nr in _instances.Values)
				{
					Debug.MasterLog("Node canditate " + nr.Connection + " " + nr.Info.Games.IsDynamic + " " + nr.Info.Games.Used
							    + " " + nr.PendingCreateRequests + " " + nr.Info.Games.MaxLimit);
					if (nr.Connection == null)
						continue;
					if (!nr.Info.Games.IsDynamic)
						continue;
					if (nr.AcceptedConfigurations == null)
						continue;
					if ((nr.Info.Games.Used + nr.PendingCreateRequests) >= nr.Info.Games.MaxLimit)
						continue;

					bool match = false;
					foreach (string pattern in nr.AcceptedConfigurations)
					{
						Regex ex = new Regex(pattern);
						if (ex.IsMatch(Configuration))
						{
							match = true;
							break;
						}
					}

					if (!match)
						continue;

					int score = 0;
					if (score > bestScore)
						candidates.Clear();

					candidates.Add(nr);
				}

				// randomize
				if (candidates.Count == 0)
				{
					Debug.MasterLog("There are no nodes left for spawning!");
					return false;
				}

				Random r = new Random();
				int pick = r.Next() % candidates.Count;

				Debug.MasterLog("Picked for create:" + pick);

				// Temp bump this number.
				candidates[pick].PendingCreateRequests++;

				// Try to create a game here...
				Netki.GameNodeCreateGameRequest nreq = new Netki.GameNodeCreateGameRequest();
				nreq.Configuration = Configuration;
				candidates[pick].Connection.SendPacket(nreq);
				return true;
			}
		}

		// Return true on done.
		public bool ProcessRequest(RequestEntry req)
		{
			if (req.Connection.IsClosed())
				return true;

			if (req.TicksWaited == 0)
			{
				switch (req.Request.type_id)
				{
					case Netki.MasterJoinedGamesRequest.TYPE_ID:
						{
							string playerId = req.Connection.GetPlayerId();

							string[] nodes = null;
							lock (_playerAtNode)
							{
								if (_playerAtNode.ContainsKey(playerId))
								{
									PlayerAtNode entry = _playerAtNode[playerId];
									nodes = entry.nodeIds.ToArray();
								}
							}

							List<GameNodeConnection> toQuery = new List<GameNodeConnection>();


							lock (_instances)
							{
								for (int i = 0; nodes != null && i < nodes.Length; i++)
								{
									if (!_instances.ContainsKey(nodes[i]))
										continue;
									toQuery.Add(_instances[nodes[i]].Connection);
								}

								//-- for test, query all --
								foreach (NodeRecord r in _instances.Values)
								{
									toQuery.Add(r.Connection);
								}
							}

							// Respond with the number of queries needed.
							Netki.MasterJoinedGamesResponse response = new Netki.MasterJoinedGamesResponse();
							response.RequestsCount = (uint)toQuery.Count;
							req.Connection.SendPacket(response);

							if (toQuery.Count > 0)
							{
								// send to all.
								Netki.GameNodeRequestGamesOnPlayer gnrq = new Netki.GameNodeRequestGamesOnPlayer();
								gnrq.PlayerId = playerId;
								gnrq.RequestId = MakeToken();

								// send these back to the player.
								ResponseRedirectionEntry redir = new ResponseRedirectionEntry();
								redir.Expires = DateTime.Now.AddSeconds(15);
								redir.References = toQuery.Count;
								redir.Where = req.Connection;

								lock (_response_redir)
								{
									_response_redir.Add(gnrq.RequestId, redir);
								}

								foreach (GameNodeConnection conn in toQuery)
									conn.SendPacket(gnrq);
							}

							return true;
						}
					case Netki.MasterJoinGameRequest.TYPE_ID:
						{
							Netki.MasterJoinGameRequest r = (Netki.MasterJoinGameRequest)req.Request;
							lock (_instances)
							{
								foreach (NodeRecord nr in _instances.Values)
								{
									if (nr.Connection == null)
										continue;
									Netki.GameNodeGameInfo[] games = nr.Info.Games.Games;
									for (int i = 0; i < games.Length; i++)
									{
										if (games[i].Id == r.GameId && games[i].JoinableByName)
										{
											req.RequestId = MakeToken();
											// No knock token since those are generated by the game node.
											Netki.GameNodeAuthPlayer auth = new Netki.GameNodeAuthPlayer();
											auth.PlayerId = req.Connection.GetPlayerId();
											auth.AuthToken = MakeToken();
											auth.GameId = r.GameId;
											auth.RequestId = req.RequestId;
											nr.Connection.SendPacket(auth);
											return false;
										}
									}
								}
							}
							// fail
							Netki.MasterJoinGameResponse resp = new Netki.MasterJoinGameResponse();
							req.Connection.SendPacket(resp);
							return true;
						}
					case Netki.MasterJoinConfigurationRequest.TYPE_ID:
						{
							Netki.MasterJoinConfigurationRequest r = (Netki.MasterJoinConfigurationRequest)req.Request;
							if (FinalizeJoinConfigurationRequest(req.Connection, r, req.RetryCounter))
								return true;

							if (!SpawnCreateGameRequest(r.Configuration))
							{
								// The idea is that other clients might have maxed out the spawn requests
								// and so there will appear games with this configuration.
								Debug.MasterLog("Spawn failed but will hang around still");
							}
						}
						return false;
				}
			}

			// Else....
			switch (req.Request.type_id)
			{
				case Netki.MasterJoinConfigurationRequest.TYPE_ID:
					if (FinalizeJoinConfigurationRequest(req.Connection, (Netki.MasterJoinConfigurationRequest)req.Request, req.RetryCounter))
						return true;
					break;
				case Netki.MasterJoinGameRequest.TYPE_ID:
					{
						// Malformed
						if (req.RequestId == null || req.RequestId.Length == 0)
						{
							Debug.MasterLog("Encountered empty request id");
							return true;
						}

						// Send back response if there is a player.
						bool retry = false;
						lock (_auth)
						{
							if (_auth.ContainsKey(req.RequestId))
							{
								Authorization auth = _auth[req.RequestId];
								Netki.MasterJoinGameResponse resp = new Netki.MasterJoinGameResponse();
								if (auth != null)
								{
									resp.Host = auth.Host;
									resp.Port = auth.Port;
									resp.KnockToken = auth.KnockToken;
									resp.AuthToken = auth.AuthToken;
									req.Connection.SendPacket(resp);
								}
								else if (req.OriginalRequest == null || req.RetryCounter > 5)
								{
									req.Connection.SendPacket(resp);
								}
								else
								{
									Debug.MasterLog("Retrying request RetryCounter=" + req.RetryCounter);
									retry = true;
								}

								_auth.Remove(req.RequestId);

								if (!retry)
									return true;
							}
						}
						if (retry)
						{
							RequestEntry recreate = new RequestEntry();
							recreate.Connection = req.Connection;
							recreate.Request = req.OriginalRequest;
							recreate.RetryCounter = req.RetryCounter + 1;
							_requests.Add(recreate);
							return true;
						}
					}
					break;
				default:
					// invalid state
					return true;
			}

			return false;
		}

		private bool FinalizeJoinConfigurationRequest(GameClientConnection connection, Netki.MasterJoinConfigurationRequest req, int retryCounter)
		{
			string gameId = null;
			lock (_instances)
			{
				foreach (NodeRecord nr in _instances.Values)
				{
					if (nr.Connection == null)
						continue;

					foreach (Netki.GameNodeGameInfo gi in nr.Info.Games.Games)
					{
						Debug.MasterLog(gi.Id + " PlayerSlotsLeft=" + gi.Status.PlayerSlotsLeft + " joinable=" + gi.JoinableByName);
						if (!gi.JoinableByConfig || !gi.JoinableByName || gi.Status.PlayerSlotsLeft < 1)
							continue;

						if (gi.Configuration == req.Configuration)
						{
							Debug.MasterLog("Matched game! Temp reducing player slots left.");
							gameId = gi.Id;
							gi.Status.PlayerSlotsLeft--;
							break;
						}
					}
					if (gameId != null)
						break;
				}
			}

			if (gameId != null)
			{
				// Now we construct a join on game id request instead
				Netki.MasterJoinGameRequest jgr = new Netki.MasterJoinGameRequest();
				jgr.GameId = gameId;

				Debug.MasterLog("Mutating request to MasterJoinGameRequest onto " + gameId);
				RequestEntry nreq = new RequestEntry();
				nreq.Connection = connection;
				nreq.Request = jgr;
				nreq.OriginalRequest = req;
				nreq.RetryCounter = retryCounter + 1;
				nreq.TicksWaited = 0;

				_requests.Add(nreq);
				return true;
			}

			return false;
		}

		public void UpdateThread()
		{
			Debug.MasterLog("Started update loop");
			int toPing = 0;
			while (true)
			{
				if (toPing++ > 20)
				{
					Netki.GameNodePing ping = new Netki.GameNodePing();
					ping.SendGamesList = true;
					ping.Time = (uint)DateTime.UtcNow.Ticks;
					lock (_instances)
					{
						int totalPlayers = 0;
						foreach (NodeRecord node in _instances.Values)
						{
							for (int q = 0; q < node.Info.Games.Games.Length; q++)
								totalPlayers += (int)node.Info.Games.Games[q].Status.PlayersJoined;
							if (node.Connection == null)
								continue;
							node.Connection.SendPacket(ping);
						}
						Debug.MasterLog("Total players:" + totalPlayers + " | CONNECTIONS = clients:" + _client_serv.GetNumConnections() + " nodes:" + _node_serv.GetNumConnections());
					}
					toPing = 0;
				}
				Thread.Sleep(100);

				// clean up
				lock (_response_redir)
				{
					DateTime cur = DateTime.Now;
					List<string> toRemove = new List<string>();
					foreach (var entry in _response_redir)
					{
						if (entry.Value.Expires < cur)
						{
							Debug.MasterLog("Response redirection timed out with references=" + entry.Value.References);
							toRemove.Add(entry.Key);
						}
					}
					foreach (string s in toRemove)
						_response_redir.Remove(s);
				}

				while (true)
				{
					bool OneMoreTime = false;
					int removed = 0;
					int start = _requests.Count;
					lock (_requests)
					{
						for (int i = 0; i < _requests.Count; i++)
						{
							RequestEntry req = _requests[i];
							if (req.TicksWaited == 0)
								OneMoreTime = true;

							const int TickLimit = 20; // 10 seconds.
							if (req.TicksWaited > TickLimit || ProcessRequest(req))
							{
								if (req.TicksWaited > TickLimit)
								{
									Debug.MasterLog("Request timed out, sending join failed..");
									Netki.MasterJoinGameResponse fail = new Netki.MasterJoinGameResponse();
									req.Connection.SendPacket(fail);
								}
								_requests.RemoveAt(i);
								i--;
								removed++;
							}
							else
							{
								req.TicksWaited++;
							}
						}
					}

					if (start > 0 || removed > 0)
					{
						Debug.MasterLog("RequestsStart:" + start + " Removed:" + removed + " Current:" + _requests.Count);
					}

					if (!OneMoreTime)
						break;
				}
			}
		}
	}
}