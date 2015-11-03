using System.Collections.Generic;
using System.Threading;
using System;

namespace Cube
{
	public class NodeMaster
	{
		public const int DEFAULT_NODE_PORT = 8100;
		public const int DEFAULT_CLIENT_PORT = 8101;

		class NodeRecord
		{
			public string Address;
			public GameNodeConnection Connection;
			public netki.GameNodeInfo Info;
			public int PendingCreateRequests;
			public float Lag;
		}

		public class RequestEntry
		{
			public netki.Packet Request, OriginalRequest;
			public GameClientConnection Connection;
			public string RequestId;
			public int TicksWaited;
			public int RetryCounter;
		}

		public class Authorization
		{
			public string Address;
			public string Token;
		}

		public class ResponseRedirectionEntry
		{
			public GameClientConnection Where;
			public DateTime Expires;
			public int References;
		}

		netki.PacketStreamServer _node_serv;
		netki.PacketStreamServer _client_serv;
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

		public NodeMaster(ApplicationPacketHandler packet_handler)
		{
			_app_packet_handler = packet_handler;
			_node_serv = new netki.PacketStreamServer(new NodeConnectionHandler(this));
			_client_serv = new netki.PacketStreamServer(new ClientConnectionHandler(this));
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
					Console.WriteLine("token from [" + mix +"] made [" + token + "]");
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

		public void RegisterInstance(netki.GameNodeInfo info, GameNodeConnection Connection)
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
				r.Address = info.NodeAddress;
				_instances.Add(info.NodeId, r);
			}
			Console.WriteLine("[master] - added instance [" + info.NodeId + "]");
		}

		// These will not be more than one per connection.
		public void OnClientRequest(netki.Packet packet, GameClientConnection conn)
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

		public void OnNodePacket(string nodeId, netki.Packet p)
		{
			switch (p.type_id)
			{
				case netki.GameNodePing.TYPE_ID:
					{
						netki.GameNodePing ping = (netki.GameNodePing) p;
						lock (_instances)
						{
							if (!_instances.ContainsKey(nodeId))
								return;

							uint diff = ((uint)DateTime.UtcNow.Ticks) - ping.Time;
							_instances[nodeId].Lag = (int)diff / 10000.0f; 
						}
						return;
					}
				case netki.GameNodePlayerIsOnGames.TYPE_ID:
					{
						netki.GameNodePlayerIsOnGames pkt = (netki.GameNodePlayerIsOnGames) p;
						Console.WriteLine("Response from [" + nodeId + "] player " + pkt.PlayerId + " is on " + pkt.GameIds.Length + " games");

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
								netki.MasterJoinedGamesResponse resp = new netki.MasterJoinedGamesResponse();
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
				case netki.GameNodeGamesList.TYPE_ID:
					{
						netki.GameNodeGamesList list = (netki.GameNodeGamesList) p;
						lock (_instances)
						{
							if (!_instances.ContainsKey(nodeId))
								return;
							_instances[nodeId].Info.Games = list;
						}
						Console.WriteLine("Games on node [" + nodeId + "] (lag:" + _instances[nodeId].Lag + " ms)");
						for (int i=0;i<list.Games.Length;i++)
						{
							Console.WriteLine("[master] @" + nodeId + " game" + i + "/" + list.MaxLimit + " [" + list.Games[i].Id + ":" + list.Games[i].Configuration + ":" + list.Games[i].Info + "] SlotsLeft=" + list.Games[i].Status.PlayerSlotsLeft + " Playres=" + list.Games[i].Status.PlayersJoined + " Rejoin=" + list.Games[i].RejoinPlayers.Length);
						}

						Console.WriteLine("--------");
						return;
					}
				case netki.GameNodeCreateGameResponse.TYPE_ID:
					{
						netki.GameNodeCreateGameResponse resp = (netki.GameNodeCreateGameResponse) p;
						if (resp.GameId != null)
						{
							Console.WriteLine("[master] - node [" + nodeId + "] created requested game [" + resp.GameId + "]");
						}
						else
						{
							// should downprioritize this node...
							Console.WriteLine("[master] - node [" + nodeId + "] failed to create requested game");
						}
						lock (_instances)
						{
							if (_instances.ContainsKey(nodeId))
							{
								_instances[nodeId].PendingCreateRequests--;
								Console.WriteLine("[master] node [" + nodeId + "] pendingRequests=" + _instances[nodeId].PendingCreateRequests);
							}
						}
						return;
					}
				case netki.GameNodeAuthPlayer.TYPE_ID:
					{
						netki.GameNodeAuthPlayer auth = (netki.GameNodeAuthPlayer) p;
						Console.WriteLine("[master] - node [" + nodeId + "] responded to auth success=" + auth.Success);
						lock (_auth)
						{
							if (_auth.ContainsKey(auth.RequestId))
								_auth.Remove(auth.RequestId);

							// store authorization
							if (auth.Success)
							{
								Authorization na = new Authorization();
								na.Address = _instances[nodeId].Address;
								na.Token = auth.Token;
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
			Console.WriteLine("Disconnecting instance " + Id);
		}


		bool SpawnCreateGameRequest(string Configuration)
		{
			lock (_instances)
			{
				List<NodeRecord> candidates = new List<NodeRecord>();
				int bestScore = 0;

				foreach (NodeRecord nr in _instances.Values)
				{
					Console.WriteLine("Node canditate " + nr.Connection + " " + nr.Info.Games.IsDynamic + " " + nr.Info.Games.Used
						+ " " + nr.PendingCreateRequests + " " + nr.Info.Games.MaxLimit);
					if (nr.Connection == null)
						continue;
					if (!nr.Info.Games.IsDynamic)
						continue;
					if ((nr.Info.Games.Used + nr.PendingCreateRequests) >= nr.Info.Games.MaxLimit)
						continue;

					int score = 0;
					if (score > bestScore)
						candidates.Clear();

					candidates.Add(nr);
				}

				// randomize
				if (candidates.Count == 0)
				{
					Console.WriteLine("There are no nodes left for spawning!");
					return false;
				}
					
				Random r = new Random();
				int pick = r.Next() % candidates.Count;

				Console.WriteLine("Picked for create:" + pick);

				// Temp bump this number.
				candidates[pick].PendingCreateRequests++;

				// Try to create a game here...
				netki.GameNodeCreateGameRequest nreq = new netki.GameNodeCreateGameRequest();
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
					case netki.MasterJoinedGamesRequest.TYPE_ID:
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
								for (int i=0;nodes != null && i<nodes.Length;i++)
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
							netki.MasterJoinedGamesResponse response = new netki.MasterJoinedGamesResponse();
							response.RequestsCount = (uint)toQuery.Count;
							req.Connection.SendPacket(response);

							if (toQuery.Count > 0)
							{
								// send to all.
								netki.GameNodeRequestGamesOnPlayer gnrq = new netki.GameNodeRequestGamesOnPlayer();
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
					case netki.MasterJoinGameRequest.TYPE_ID:
						{
							netki.MasterJoinGameRequest r = (netki.MasterJoinGameRequest) req.Request;
							lock (_instances)
							{
								foreach (NodeRecord nr in _instances.Values)
								{
									if (nr.Connection == null)
										continue;
									netki.GameNodeGameInfo[] games = nr.Info.Games.Games;
									for (int i=0;i<games.Length;i++)
									{
										if (games[i].Id == r.GameId && games[i].JoinableByName)
										{
											// make request id
											req.RequestId = MakeToken();
											// success
											netki.GameNodeAuthPlayer auth = new netki.GameNodeAuthPlayer();
											auth.PlayerId = req.Connection.GetPlayerId();
											auth.Token = MakeToken();
											auth.GameId = r.GameId;
											auth.RequestId = req.RequestId;
											nr.Connection.SendPacket(auth);
											return false;
										}
									}
								}
							}
							// fail
							netki.MasterJoinGameResponse resp = new netki.MasterJoinGameResponse();
							req.Connection.SendPacket(resp);
							return true;
						}
					case netki.MasterJoinConfigurationRequest.TYPE_ID:
						{
							netki.MasterJoinConfigurationRequest r = (netki.MasterJoinConfigurationRequest) req.Request;
							if (FinalizeJoinConfigurationRequest(req.Connection, r, req.RetryCounter))
								return true;

							if (!SpawnCreateGameRequest(r.Configuration))
							{
								// The idea is that other clients might have maxed out the spawn requests
								// and so there will appear games with this configuration.
								Console.WriteLine("Spawn failed but will hang around still");
							}
						}
						return false;
				}
			}

			// Else....
			switch (req.Request.type_id)
			{
				case netki.MasterJoinConfigurationRequest.TYPE_ID:
					if (FinalizeJoinConfigurationRequest(req.Connection, (netki.MasterJoinConfigurationRequest) req.Request, req.RetryCounter))
						return true;
					break;
				case netki.MasterJoinGameRequest.TYPE_ID:
					{
						// Malformed
						if (req.RequestId == null || req.RequestId.Length == 0)
						{
							Console.WriteLine("Encountered empty request id");
							return true;
						}

						// Send back response if there is a player.
						bool retry = false;
						lock (_auth)
						{
							if (_auth.ContainsKey(req.RequestId))
							{
								Authorization auth = _auth[req.RequestId];
								netki.MasterJoinGameResponse resp = new netki.MasterJoinGameResponse();
								if (auth != null)
								{
									resp.Address = auth.Address;
									resp.Token = auth.Token;
									req.Connection.SendPacket(resp);
								}
								else if (req.OriginalRequest == null || req.RetryCounter > 5)
								{
									req.Connection.SendPacket(resp);
								}
								else 
								{
									Console.WriteLine("Retrying request RetryCounter=" + req.RetryCounter);
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

		private bool FinalizeJoinConfigurationRequest(GameClientConnection connection, netki.MasterJoinConfigurationRequest req, int retryCounter)
		{
			string gameId = null;
			lock (_instances)
			{
				foreach (NodeRecord nr in _instances.Values)
				{
					if (nr.Connection == null)
						continue;
					
					foreach (netki.GameNodeGameInfo gi in nr.Info.Games.Games)
					{
						Console.WriteLine(gi.Id + " PlayerSlotsLeft=" + gi.Status.PlayerSlotsLeft + " joinable=" + gi.JoinableByName);
						if (!gi.JoinableByConfig || !gi.JoinableByName || gi.Status.PlayerSlotsLeft < 1)
							continue;

						if (gi.Configuration == req.Configuration)
						{
							Console.WriteLine("Matched game! Temp reducing player slots left.");
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
				netki.MasterJoinGameRequest jgr = new netki.MasterJoinGameRequest();
				jgr.GameId = gameId;

				Console.WriteLine("[master] - mutating request to MasterJoinGameRequest onto " + gameId);
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
			Console.WriteLine("[master] - started update loop");
			int toPing = 0;
			while (true)
			{
				if (toPing++ > 20)
				{
					netki.GameNodePing ping = new netki.GameNodePing();
					ping.SendGamesList = true;
					ping.Time = (uint)DateTime.UtcNow.Ticks;
					lock (_instances)
					{
						int totalPlayers = 0;
						foreach (NodeRecord node in _instances.Values)
						{
							for (int q=0;q<node.Info.Games.Games.Length;q++)
								totalPlayers += (int)node.Info.Games.Games[q].Status.PlayersJoined;
							if (node.Connection == null)
								continue;
							node.Connection.SendPacket(ping);
						}
						Console.WriteLine("Total players:" + totalPlayers + " | CONNECTIONS = clients:" + _client_serv.GetNumConnections() + " nodes:" + _node_serv.GetNumConnections());
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
							Console.WriteLine("Response redirection timed out with references=" + entry.Value.References);
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
						for (int i=0;i<_requests.Count;i++)
						{
							RequestEntry req =_requests[i];
							if (req.TicksWaited == 0)
								OneMoreTime = true;

							const int TickLimit = 100; // 10 seconds.
							if (req.TicksWaited > TickLimit || ProcessRequest(req))
							{
								if (req.TicksWaited > TickLimit)
								{
									Console.WriteLine("Request timed out, sending join failed..");
									netki.MasterJoinGameResponse fail = new netki.MasterJoinGameResponse();
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
						Console.WriteLine("RequestsStart:" + start + " Removed:" + removed + " Current:" + _requests.Count);

					if (!OneMoreTime)
						break;
				}
			}
		}
	}
}