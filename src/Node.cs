using System.Collections.Generic;
using System.Threading;
using System.Net.Sockets;
using System.Net;
using System;

namespace Cube
{
	public struct Authorization
	{
		public string PlayerId;
		public string AuthToken;
		public string KnockToken;
		public DateTime Created;
	}

	public delegate bool GetAuthInformation(string Token, out Authorization Auth);

	public interface IGameSpawner
	{
		IGameInstServer SpawnInstance(string Configuration, GetAuthInformation Auther);
	}

	public class GameInstRecord
	{
		public IGameInstServer server;
		public string configuration;
		public string info;
		public string id;
		public List<Authorization> auth;
		public Dictionary<string, DateTime> rejoin = new Dictionary<string, DateTime>();
		public DateTime lastActive;
		public ServerDatagram[] datagrams;
		public uint numDatagrams;
	}

	public class Node
	{
		ApplicationPacketHandler _app_packet_handler;
		List<GameInstRecord> _instances = new List<GameInstRecord>();
		Thread _masterThread, _updateThread;
		IGameSpawner _spawner;
		string _id;
		bool _isDynamic;
		int _maxInstances;
		int _idCounter;
		int _updateRate;

		string _masterAddress;

		string[] _configurations;

		public Node(IGameSpawner spawner, string id, string[] configurations, int maxInstances, int updateRateMs, string masterAddress)
		{
			_app_packet_handler = new MasterPacketsHandler();
			_masterAddress = masterAddress;
			_configurations = configurations;
			_masterThread = new Thread(MasterThread);
			_updateThread = new Thread(UpdateThread);
			_spawner = spawner;
			_isDynamic = true;
			_maxInstances = maxInstances;
			_idCounter = 0;
			_id = id;
			_updateRate = updateRateMs;
			_updateThread.Start();
		}

		Dictionary<ulong, IGameInstServer> _playerDatagrams = new Dictionary<ulong, IGameInstServer>();

		public ApplicationPacketHandler GetPacketHandler()
		{
			return _app_packet_handler;
		}

		public void Start()
		{
			_masterThread.Start();
		}

		public string StartInstance(string Configuration, string Info)
		{
			GameInstRecord n = new GameInstRecord();
			n.server = _spawner.SpawnInstance(Configuration, null);
			if (n.server == null)
				return null;

			n.lastActive = DateTime.Now;
			n.info = Info;
			n.configuration = Configuration;
			n.id = _id + "-" + n.server.GetVersionString() + "-" + (_idCounter++);
			n.auth = new List<Authorization>();

			lock (_instances)
			{
				_instances.Add(n);
				return n.id;
			}
		}

		public void AddRejoinEntry(string gameId, GameInstPlayer player, DateTime expiry)
		{
			lock (_instances)
			{
				for (int i = 0; i < _instances.Count; i++)
				{
					GameInstRecord record = _instances[i];
					if (record.id == gameId)
					{
						if (record.rejoin.ContainsKey(player.name))
						{
							record.rejoin.Remove(player.name);
						}

						Debug.NodeLog("Player " + player.name + " can rejoin onto " + record.id + " until " + expiry);
						record.rejoin.Add(player.name, expiry);
					}
				}
			}
		}

		public void RemoveAuthsAndGames()
		{
			lock (_instances)
			{
				for (int i = 0; i < _instances.Count; i++)
				{
					bool havePending = false;
					DateTime now = DateTime.Now;
					List<Authorization> auths = _instances[i].auth;
					for (int j = 0; j < auths.Count; j++)
					{
						TimeSpan diff = now - auths[j].Created;
						if (diff.TotalSeconds > 15)
						{
							Debug.NodeLog("Expiring auth [" + auths[j].KnockToken + "] to game [" + _instances[i].id + "]");
							auths.RemoveAt(j--);
						}
						else
						{
							havePending = true;
						}
					}

					if (!havePending && _instances[i].server.CanShutdown())
					{
						TimeSpan age = DateTime.Now - _instances[i].lastActive;
						if (age.TotalSeconds > 15)
						{
							Debug.NodeLog("Removing game instance " + _instances[i].id);
							try 
							{
								_instances[i].server.Shutdown();
							}
							catch (Exception e)
							{
								Debug.NodeLog("Ecxeption during shutdown: " + e.ToString());
							}
							_instances.RemoveAt(i--);
						}
					}
					else
					{
						_instances[i].lastActive = DateTime.Now;
					}
				}
			}
			System.GC.Collect();
		}

		public Netki.GameNodeGamesList MakeGamesList()
		{
			lock (this)
			{
				Netki.GameNodeGamesList list = new Netki.GameNodeGamesList();
				list.IsDynamic = _isDynamic;
				list.MaxLimit = (uint)_maxInstances;
				list.Used = (uint)_instances.Count;
				list.Games = new Netki.GameNodeGameInfo[_instances.Count];

				List<string> rejoinPlayers = new List<string>();
				for (int i = 0; i < _instances.Count; i++)
				{
					list.Games[i] = new Netki.GameNodeGameInfo();
					list.Games[i].Configuration = _instances[i].configuration;
					list.Games[i].Id = _instances[i].id;
					list.Games[i].Info = _instances[i].info;
					list.Games[i].Status = _instances[i].server.GetStatus();
					list.Games[i].Address = _instances[i].server.GetAddress();

					foreach (string pl in _instances[i].rejoin.Keys)
					{
						if (_instances[i].rejoin[pl] > DateTime.Now)
							rejoinPlayers.Add(pl);
					}
					list.Games[i].RejoinPlayers = rejoinPlayers.ToArray();
					rejoinPlayers.Clear();

					// reduce by pending auths.
					int reduce = _instances[i].auth.Count;
					if (reduce > list.Games[i].Status.PlayerSlotsLeft)
						list.Games[i].Status.PlayerSlotsLeft = 0;
					else
						list.Games[i].Status.PlayerSlotsLeft -= (uint)reduce;

					list.Games[i].JoinableByConfig = true;
					list.Games[i].JoinableByName = true;//list.Games[i].Status.PlayerSlotsLeft > 0;
				}
				return list;
			}
		}


		System.Random _token_random = new System.Random();
		uint _token_counter = 0;

		public string MakeKnockToken()
		{
			lock (_token_random)
			{
				using (var sha = System.Security.Cryptography.SHA256.Create())
				{
					string mix = "token-" + _token_random.NextDouble() + "-knock-" + DateTime.Now + "_" + DateTime.Now.Millisecond + "_" + (_token_counter);
					var computedHash = sha.ComputeHash(System.Text.Encoding.ASCII.GetBytes(mix));
					var token = Convert.ToBase64String(computedHash);
					Debug.NodeLog("Knock token => " + token);
					return token;
				}
			}
		}

		public void OnMasterPacket(Netki.Packet packet, PacketExchangeDelegate exchange)
		{
			switch (packet.type_id)
			{
				case Netki.GameNodePing.TYPE_ID:
					{
						exchange(packet);
						RemoveAuthsAndGames();
						Netki.GameNodePing p = (Netki.GameNodePing)packet;
						if (p.SendGamesList)
							exchange(MakeGamesList());
						return;
					}
				case Netki.GameNodeRequestGamesOnPlayer.TYPE_ID:
					{
						Netki.GameNodeRequestGamesOnPlayer req = (Netki.GameNodeRequestGamesOnPlayer)packet;
						List<string> gameIds = new List<string>();
						lock (_instances)
						{
							foreach (GameInstRecord r in _instances)
							{
								if (r.server.CanPlayerReconnect(req.PlayerId))
									gameIds.Add(r.id);
							}
						}

						Netki.GameNodePlayerIsOnGames resp = new Netki.GameNodePlayerIsOnGames();
						resp.GameIds = gameIds.ToArray();
						resp.PlayerId = req.PlayerId;
						resp.RequestId = req.RequestId;
						exchange(resp);
						return;
					}

				case Netki.GameNodeAuthPlayer.TYPE_ID:
					{
						Netki.GameNodeAuthPlayer ap = (Netki.GameNodeAuthPlayer)packet;
						lock (_instances)
						{
							foreach (GameInstRecord r in _instances)
							{
								if (r.id == ap.GameId)
								{
									int left = (int)r.server.GetStatus().PlayerSlotsLeft;
									left -= r.auth.Count;
									if (left > 0)
									{
										Authorization auth = new Authorization();
										auth.PlayerId = ap.PlayerId;
										auth.AuthToken = ap.AuthToken;
										auth.KnockToken = ap.KnockToken;
										auth.Created = DateTime.Now;
										r.auth.Add(auth);
										// send packet back as ack.
										ap.Success = true;
										ap.Address = r.server.GetAddress();
										ap.KnockToken = MakeKnockToken();
										r.server.GiveKnockTocken(ap.KnockToken, delegate
										{
											lock (_instances)
											{
												for (int i = 0; i < r.auth.Count; i++)
												{
													if (r.auth[i].KnockToken == auth.KnockToken)
														r.auth.RemoveAt(i);
												}
											}
										});
										exchange(ap);
									}
									else
									{
										ap.Success = false;
										exchange(ap);
									}
									break;
								}
							}
						}
						return;
					}

				case Netki.GameNodeCreateGameRequest.TYPE_ID:
					{
						Netki.GameNodeCreateGameRequest req = (Netki.GameNodeCreateGameRequest)packet;

						lock (this)
						{
							if (!_isDynamic || _instances.Count >= _maxInstances)
							{
								Netki.GameNodeCreateGameResponse resp = new Netki.GameNodeCreateGameResponse();
								exchange(resp);
								return;
							}
						}

						// note that this handles both err and succ
						Netki.GameNodeCreateGameResponse rp = new Netki.GameNodeCreateGameResponse();
						rp.GameId = StartInstance(req.Configuration, "auto");
						exchange(MakeGamesList());
						exchange(rp);
						return;
					}
				default:
					{
						Debug.NodeLog("Unexpected packet from master");
						return;
					}
			}
		}

		private void UpdateThread()
		{
			DateTime next = DateTime.Now.AddTicks(10000 * _updateRate);
			ServerDatagram[] sd = new ServerDatagram[1024];
			while (true)
			{
				DateTime now = DateTime.Now;
				if (now > next)
				{
					lock (_instances)
					{
						foreach (GameInstRecord r in _instances)
						{
							try
							{
								r.server.Update();
							}
							catch (Exception e)
							{
								Debug.NodeLog("Exception during update!" + e.ToString());
							}
						}
					}
					next = next.AddMilliseconds(_updateRate);
				}

				TimeSpan ts = (next - now);
				if (ts.TotalMilliseconds > 0)
				{
					DateTime prev = DateTime.Now;
					Thread.Sleep((int)(ts.TotalMilliseconds + 1));
					if ((DateTime.Now - prev).TotalMilliseconds > ts.TotalMilliseconds * 10)
					{
						Debug.NodeLog("Sleep for " + ts.TotalMilliseconds + " => " + (DateTime.Now - prev).TotalMilliseconds);
					}
				}
			}
		}

		private void MasterThread()
		{
			while (true)
			{
				IPHostEntry ipHostInfo = Dns.GetHostEntry(_masterAddress);
				IPAddress ipAddress = null;

				foreach (var p in ipHostInfo.AddressList)
				{
					if (p.AddressFamily == AddressFamily.InterNetwork)
						ipAddress = p;
				}

				IPEndPoint remoteEP = new IPEndPoint(ipAddress, NodeMaster.DEFAULT_NODE_PORT);
				Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

				// Connect the socket to the remote endpoint. Catch any errors.
				try
				{
					Debug.NodeLog("Connecting to master " + _masterAddress);
					socket.Connect(remoteEP);

					Debug.NodeLog("Sending id packet");
					Netki.GameNodeInfo info = new Netki.GameNodeInfo();
					info.Games = MakeGamesList();
					info.NodeId = _id;
					Netki.Bitstream.Buffer buf = _app_packet_handler.MakePacket(info);
					socket.Send(buf.buf, 0, (int)buf.bytesize, 0);

					BufferedPacketDecoder pdec = new BufferedPacketDecoder(65536, _app_packet_handler);
					byte[] rbuf = new byte[65536];

					PacketExchangeDelegate xchange = delegate (Netki.Packet p)
					{
						Netki.Bitstream.Buffer b = _app_packet_handler.MakePacket(p);
						socket.Send(b.buf, 0, (int)b.bytesize, 0);
					};

					Netki.GameNodeConfigurationsSupport conf = new Netki.GameNodeConfigurationsSupport();
					conf.Patterns = _configurations;
					xchange(conf);

					while (true)
					{
						int read = socket.Receive(rbuf);
						if (read <= 0)
						{
							Debug.NodeLog("Disconnected from master");
							break;
						}

						pdec.OnStreamData(rbuf, 0, read, delegate (Netki.DecodedPacket packet)
						{
							OnMasterPacket(packet.packet, xchange);
						});
					}
				}
				catch (SocketException se)
				{
					Debug.NodeLog("SocketException happened. Retrying : " + se.ToString());
					Random r = new Random();
					Thread.Sleep(r.Next() % 2000 + 500);
				}
				catch (Exception e)
				{
					Debug.NodeLog("Unexpected exception : " + e.ToString());
				}
				Thread.Sleep(500);
			}
		}
	}
}
