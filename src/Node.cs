using System.Collections.Generic;
using System.Threading;
using System.Net.Sockets;
using System.Net;
using System;

namespace Cube
{
	public interface IGameSpawner
	{
		IGameInstServer SpawnInstance(string Configuration);
	}

	public struct Authorization
	{
		public string PlayerId;
		public string Token;
		public DateTime Created;
	}

	public struct UDPAuthorization
	{
		public byte[] Key;
		public IGameInstServer Server;
		public string PlayerId;
		public DateTime Expires;
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
	}

	public class PlayerConnection : GameInstPlayer, netki.StreamConnection
	{
		private netki.BufferedPacketDecoder _decoder;
		private Node _node;
		private PacketExchangeDelegate _packet_target;
		netki.ConnectionOutput _output;
		private IGameInstServer _server;

		public PlayerConnection(Node node, netki.ConnectionOutput output)
		{
			_node = node;
			_decoder = new netki.BufferedPacketDecoder(512, node.GetPacketHandler());
			_output = output;
		}

		public void SetPacketTarget(PacketExchangeDelegate xchange)
		{
			_packet_target = xchange;
		}

		public void OnDisconnected()
		{
			if (_server != null)
			{
				_server.DisconnectPlayer(this);
			}
		}

		public void Send(netki.Packet packet)
		{
			netki.Bitstream.Buffer buf = _node.GetPacketHandler().MakePacket(packet);
			_output.Send(buf.buf, 0, buf.bufsize);
		}

		public void OnPacket(netki.DecodedPacket pkt)
		{
			switch (pkt.packet.type_id)
			{
				case netki.GameNodeAuth.TYPE_ID:
					{
						if (_server == null)
						{
							netki.GameNodeAuth auth = (netki.GameNodeAuth) pkt.packet;
							GameInstRecord r = _node.DoPlayerAuth(auth, this);
							if (r != null)
							{
								_server = r.server;
							}
						}
					}
					break;
				default:
					{
						if (_packet_target != null)
							_packet_target(pkt.packet);
						break;
					}
			}
			Console.WriteLine("got player packet id " + pkt.type_id);
		}

		public void OnStreamData(byte[] data, int offset, int length)
		{
			_decoder.OnStreamData(data, offset, length, OnPacket);
		}
	}

	public class PlayerConnectionHandler : netki.StreamConnectionHandler
	{
		private Node _node;

		public PlayerConnectionHandler(Node node)
		{
			_node = node;
		}

		public void OnStartup()
		{

		}

		public netki.StreamConnection OnConnected(int connection_id, netki.ConnectionOutput output)
		{
			return new PlayerConnection(_node, output);
		}
	}
		
	public class Node
	{
		ApplicationPacketHandler _app_packet_handler;
		netki.PacketStreamServer _player_serv;
		netki.PacketDatagramServer _dgram_serv;
		List<GameInstRecord> _instances = new List<GameInstRecord>();
		Thread _masterThread, _updateThread;
		IGameSpawner _spawner;
		string _id;
		bool _isDynamic;
		int _maxInstances;
		int _idCounter;
		int _port;
		int _updateRate;

		public Node(IGameSpawner spawner, ApplicationPacketHandler handler, string id, int maxInstances, int updateRateMs)
		{
			_player_serv = new netki.PacketStreamServer(new PlayerConnectionHandler(this));
			_dgram_serv = new netki.PacketDatagramServer(OnDatagram);
			_app_packet_handler = handler;
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

		public static int bPort = 0;

		public static uint _udpAuthKeyCounter = 1234;
		Dictionary<ulong, UDPAuthorization> _udpAuthorization = new Dictionary<ulong, UDPAuthorization>();
		Dictionary<ulong, IGameInstServer> _playerDatagrams = new Dictionary<ulong, IGameInstServer>();

		int _playerDatagramCleanup = 0;

		public ApplicationPacketHandler GetPacketHandler()
		{
			return _app_packet_handler;
		}

		// Called from one worker thread only.
		public void OnDatagram(byte[] data, ulong endpoint)
		{
			if (++_playerDatagramCleanup > 2048)
			{
				_playerDatagramCleanup = 0;
			}

			netki.Bitstream.Buffer b = new netki.Bitstream.Buffer();
			b.buf = data;
			b.bufsize = data.Length;
			int pkt_id = (int)netki.Bitstream.ReadBits(b, 16);

			if (_playerDatagrams.ContainsKey(endpoint))
			{
				// initialization messages
				if (pkt_id != 0xffff)
					_playerDatagrams[endpoint].OnDatagram(data, 0, data.Length, endpoint);
			}
			else
			{
				netki.GameNodeUnreliableAuth auth = new netki.GameNodeUnreliableAuth();
				if (netki.GameNodeUnreliableAuth.ReadFromBitstream(b, auth))
				{
					lock (_udpAuthorization)
					{
						if (_udpAuthorization.ContainsKey(auth.AuthId))
						{
							UDPAuthorization record = (UDPAuthorization) _udpAuthorization[auth.AuthId];
							if (record.Key.Length != auth.Key.Length)
								return;

							for (int i = 0; i < record.Key.Length; i++)
							{
								if (record.Key[i] != auth.Key[i])
									return;
							}
								
							netki.GameNodeUnreliableAuthResponse resp = new netki.GameNodeUnreliableAuthResponse();
							resp.AuthId = auth.AuthId;
							resp.Time = auth.Time;
							netki.Bitstream.Buffer buf = netki.Bitstream.Buffer.Make(new byte[1024]);
							netki.Bitstream.PutBits(buf, 16, 0xffff);
							netki.GameNodeUnreliableAuthResponse.WriteIntoBitstream(buf, resp);
							buf.Flip();
							_dgram_serv.Send(buf.buf, 0, buf.bufsize, endpoint);

							record.Server.ConnectPlayerDatagram(record.PlayerId, endpoint, delegate(netki.Packet packet) {
                                if (packet.type_id == netki.GameNodeRawDatagramWrapper.TYPE_ID)
                                {
                                    // Attach 2 byte header where first is 0xfe for raw packet.
                                    netki.GameNodeRawDatagramWrapper wrap = (netki.GameNodeRawDatagramWrapper) packet;
                                    _dgram_serv.Send(wrap.Data, wrap.Offset, wrap.Length, endpoint);
                                }
                                else
                                {
    								netki.Bitstream.Buffer bd = _app_packet_handler.MakePacket(packet);
    								_dgram_serv.Send(bd.buf, 0, bd.bufsize, endpoint);
                                }
							});
						}
					}
				}
			}
		}

		public void Start()
		{
			_port = 9000 + bPort;
			_player_serv.Start(_port);
			_dgram_serv.Start(_port);
			bPort++;
			_masterThread.Start();
		}

		public string StartInstance(string Configuration, string Info)
		{
			GameInstRecord n = new GameInstRecord();
			n.server = _spawner.SpawnInstance(Configuration);
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
				for (int i=0;i<_instances.Count;i++)
				{
					GameInstRecord record = _instances[i];
					if (record.id == gameId)
					{
						if (record.rejoin.ContainsKey(player.name))
							record.rejoin.Remove(player.name);

						Console.WriteLine("Player " + player.name + " can rejoin onto " + record.id + " until " + expiry);
						record.rejoin.Add(player.name, expiry);
					}
				}
			}
		}

		public GameInstRecord DoPlayerAuth(netki.GameNodeAuth auth, PlayerConnection conn)
		{
			GameInstRecord server = null;
			lock (_instances)
			{
				for (int i=0;i<_instances.Count;i++)
				{
					GameInstRecord r = _instances[i];
					for (int j=0;j<r.auth.Count;j++)
					{
						if (r.auth[j].Token == auth.Token)
						{
							Console.WriteLine("Player [" + r.auth[j].PlayerId + "] connected on node " + r.id);
							conn.name = r.auth[j].PlayerId;
							server = r;
							r.auth.RemoveAt(j);
							break;
						}
					}
					if (server != null)
						break;
				}
			}

			netki.GameNodeAuthResponse resp = new netki.GameNodeAuthResponse();

			if (server != null)
			{
				resp.AuthSuccess = true;

				conn.SetPacketTarget(delegate(netki.Packet packet) {
					server.server.PacketOnPlayer(conn, packet);
				});

				PacketExchangeDelegate xchange = delegate(netki.Packet p) {
					conn.Send(p);
				};

				if (server.server.ConnectPlayerStream(conn.name, conn, xchange))
				{
					resp.JoinSuccess = true;

					// Create a new UDP authorization thing.
					UDPAuthorization ua = new UDPAuthorization();
					ua.Key = System.Text.UTF8Encoding.UTF8.GetBytes(auth.Token);
					ua.PlayerId = conn.name;
					ua.Server = server.server;

					uint id;
					lock (_udpAuthorization)
					{
						id = _udpAuthKeyCounter++;
						_udpAuthorization.Add(id, ua);
					}
					netki.GameNodeSetupUnreliable setup = new netki.GameNodeSetupUnreliable();
					setup.Host = _dgram_serv.GetHost();
					setup.Port = (uint) _dgram_serv.GetPort();
					setup.AuthId = id;
					setup.Key = ua.Key;
					conn.Send(setup);
				}
			}

			conn.Send(resp);
			return server;
		}

		public void RemoveAuthsAndGames()
		{
			lock (_instances)
			{
				for (int i=0;i<_instances.Count;i++)
				{
					bool havePending = false;
					DateTime now = DateTime.Now;
					List<Authorization> auths = _instances[i].auth;
					for (int j=0;j<auths.Count;j++)
					{
						TimeSpan diff = now - auths[j].Created;
						if (diff.TotalSeconds > 15)
						{
							Console.WriteLine("Expiring auth [" + auths[j].Token + "] to game [" + _instances[i].id + "]");
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
							Console.WriteLine("Removing game instance "+ _instances[i].id);
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

		public netki.GameNodeGamesList MakeGamesList()
		{
			lock (this)
			{
				netki.GameNodeGamesList list = new netki.GameNodeGamesList();
				list.IsDynamic =_isDynamic;
				list.MaxLimit = (uint)_maxInstances;
				list.Used = (uint)_instances.Count;
				list.Games = new netki.GameNodeGameInfo[_instances.Count];

				List<string> rejoinPlayers = new List<string>();
				for (int i=0;i<_instances.Count;i++)
				{
					list.Games[i] = new netki.GameNodeGameInfo();
					list.Games[i].Configuration = _instances[i].configuration;
					list.Games[i].Id = _instances[i].id;
					list.Games[i].Info = _instances[i].info;
					list.Games[i].Status = _instances[i].server.GetStatus();

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

		public void OnMasterPacket(netki.Packet packet, PacketExchangeDelegate exchange)
		{
			switch (packet.type_id)
			{
				case netki.GameNodePing.TYPE_ID:
					{
						exchange(packet);
						RemoveAuthsAndGames();
						netki.GameNodePing p = (netki.GameNodePing) packet;
						if (p.SendGamesList)
							exchange(MakeGamesList());
						return;
					}
				case netki.GameNodeRequestGamesOnPlayer.TYPE_ID:
					{
						netki.GameNodeRequestGamesOnPlayer req = (netki.GameNodeRequestGamesOnPlayer) packet;
						List<string> gameIds = new List<string>();
						lock (_instances)
						{
							foreach (GameInstRecord r in _instances)
							{
								if (r.server.CanPlayerReconnect(req.PlayerId))
									gameIds.Add(r.id);
							}
						}

						netki.GameNodePlayerIsOnGames resp = new netki.GameNodePlayerIsOnGames();
						resp.GameIds = gameIds.ToArray();
						resp.PlayerId = req.PlayerId;
						resp.RequestId = req.RequestId;
						exchange(resp);
						return;
					}

				case netki.GameNodeAuthPlayer.TYPE_ID:
					{
						netki.GameNodeAuthPlayer ap = (netki.GameNodeAuthPlayer) packet;
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
										auth.Token = ap.Token;
										auth.Created = DateTime.Now;
										r.auth.Add(auth);
										// send packet back as ack.
										ap.Success = true;
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
						
				case netki.GameNodeCreateGameRequest.TYPE_ID:
					{
						netki.GameNodeCreateGameRequest req = (netki.GameNodeCreateGameRequest) packet;

						lock (this)
						{
							if (!_isDynamic || _instances.Count >= _maxInstances)
							{
								netki.GameNodeCreateGameResponse resp = new netki.GameNodeCreateGameResponse();
								exchange(resp);
								return;
							}
						}

						// note that this handles both err and succ
						netki.GameNodeCreateGameResponse rp = new netki.GameNodeCreateGameResponse();
						rp.GameId = StartInstance(req.Configuration, "auto");
						exchange(MakeGamesList());
						exchange(rp);
						return;
					}
				default:
					{
						Console.WriteLine("[node] - unexpected packet from master");
						return;
					}
			}
		}

		private void UpdateThread()
		{
			DateTime next = DateTime.Now.AddTicks(10000 * _updateRate);
			while (true)
			{
				DateTime now = DateTime.Now;
				int ticks = 0;
				while (now > next && ticks < 5)
				{
					lock (_instances)
					{
						float dt = _updateRate * 0.001f;
						foreach (GameInstRecord r in _instances)
						{
							r.server.Update(dt);
						}
					}
					next = next.AddMilliseconds(_updateRate);
					ticks++;
				}

				int skips = 0;
				while (next < now)
				{
					next = next.AddMilliseconds(_updateRate);
					skips++;
				}

				if (skips > 0)
					Console.WriteLine("Updated " + ticks + " in one go [skips=" + skips + "]");

				TimeSpan ts = (next -now);
				if (ts.TotalMilliseconds > 0)
				{
					Thread.Sleep((int)(ts.TotalMilliseconds + 1));
				}
			}
		}

		private void MasterThread()
		{
			string addr = "localhost";
			while (true)
			{
				IPHostEntry ipHostInfo = Dns.GetHostEntry(addr);
				IPAddress ipAddress = ipHostInfo.AddressList[0];
				IPEndPoint remoteEP = new IPEndPoint(ipAddress, NodeMaster.DEFAULT_NODE_PORT);
				Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

				// Connect the socket to the remote endpoint. Catch any errors.
				try
				{
					Console.WriteLine("[node] - connecting to master");
					socket.Connect(remoteEP);

					Console.WriteLine("[node] - sending id packet");
					netki.GameNodeInfo info = new netki.GameNodeInfo();
					info.Games = MakeGamesList();
					info.NodeId = _id;
					info.NodeAddress = "localhost:" + _port;
					netki.Bitstream.Buffer buf = _app_packet_handler.MakePacket(info);
					socket.Send(buf.buf, 0, buf.bufsize, 0);

					netki.BufferedPacketDecoder pdec = new netki.BufferedPacketDecoder(65536, _app_packet_handler);
					byte[] rbuf = new byte[65536];

					PacketExchangeDelegate xchange = delegate(netki.Packet p) {
						netki.Bitstream.Buffer b = _app_packet_handler.MakePacket(p);
						socket.Send(b.buf, 0, b.bufsize, 0);
					};

					while (true)
					{
						int read = socket.Receive(rbuf);
						if (read <= 0)
						{
							Console.WriteLine("[node] - disconnected from master");
							break;
						}

						pdec.OnStreamData(rbuf, 0, read, delegate(netki.DecodedPacket packet) {
							OnMasterPacket(packet.packet, xchange);
						});
					}
				}
				catch (SocketException se)
				{
					Console.WriteLine("SocketException happened. Retrying : {0}", se.ToString());
					Random r = new Random();
					Thread.Sleep(r.Next()%2000 + 500);
				}
				catch (Exception e)
				{
					Console.WriteLine("Unexpected exception : {0}", e.ToString());
				}
				Thread.Sleep(500);
			}
		}
	}
}