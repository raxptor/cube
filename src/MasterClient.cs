using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;

namespace Cube
{
	public class MasterClient
	{
		public enum Status
		{
			IDLE,
			CONNECTING,
			WAITING,
			FAILED,
			DONE
		}

		string _host;
		Status _status;
		Thread _thread;
		Socket _socket;
		System.UInt32? _rejoinQueries = null;

		List<Netki.Packet> _requests = new List<Netki.Packet>();
		List<string> _joinedGames = new List<string>();
		Netki.MasterJoinGameResponse _joinGameResponse = null;
		ApplicationPacketHandler _pkg_handler;
		object _lk_result = new object();
		bool _done = false;

		public MasterClient(string host)
		{
			_host = host;
			_pkg_handler = new MasterPacketsHandler();
			_status = Status.IDLE;
			_thread = new Thread(Run);
			_thread.Start();

			// _thread takes ownership over _status
			// we keep ownership of _done
		}

		public bool AnonymousAuth(string Playername)
		{
			Netki.MasterAuthenticateAnonymous auth = new Netki.MasterAuthenticateAnonymous();
			auth.Playername = Playername;
			return QueueRequest(auth);
		}

		public bool QueryRejoins()
		{
			return QueueRequest(new Netki.MasterJoinedGamesRequest());
		}

		public bool JoinConfiguration(string Configuration)
		{
			Netki.MasterJoinConfigurationRequest req = new Netki.MasterJoinConfigurationRequest();
			req.Configuration = Configuration;
			return QueueRequest(req);
		}

		public bool QueueRequest(Netki.Packet packet)
		{
			lock (_requests)
			{
				if (_socket != null)
				{
					Netki.Bitstream.Buffer buf = _pkg_handler.MakePacket(packet);
					try
					{
						if (buf.bitsize != 0)
						{
							Console.WriteLine("Bitsize != 0!");
						}
						_socket.Send(buf.buf, 0, (int)buf.bytesize, 0);
						return true;
					}
					catch (Exception)
					{
						return false;
					}
				}
				else
				{
					_requests.Add(packet);
					return true;
				}
			}
		}

		public string[] GetJoinedGames()
		{
			return _joinedGames.ToArray();
		}

		public void OnServerPacket(Netki.DecodedPacket packet)
		{
			lock (this)
			{
				switch (packet.type_id)
				{
					case Netki.MasterJoinedGamesResponse.TYPE_ID:
						{
							Netki.MasterJoinedGamesResponse resp = (Netki.MasterJoinedGamesResponse)packet.packet;
							// initial packet contains no game ids but a request count (which might be zero)
							if (resp.GameIds == null)
							{
								_rejoinQueries = resp.RequestsCount;
							}
							else
							{
								_rejoinQueries--;
								foreach (string s in resp.GameIds)
									_joinedGames.Add(s);
							}

							Console.WriteLine("[masterclient] - rejoin queries = " + _rejoinQueries.Value);
							if (_rejoinQueries.Value == 0)
							{
								Console.WriteLine("[masterclient] - i know the games now!");
							}
						}
						break;
					case Netki.MasterJoinGameResponse.TYPE_ID:
						lock  (_lk_result)
						{
							_joinGameResponse = (Netki.MasterJoinGameResponse)packet.packet;
						}
						break;
					default:
						break;
				}
			}
		}

		public Netki.MasterJoinGameResponse GetJoinGameResponse()
		{
			lock (_lk_result)
			{
				Netki.MasterJoinGameResponse resp = _joinGameResponse;
				_joinGameResponse = null;
				return resp;
			}
		}

		public System.UInt32? GetRejoinQueries()
		{
			lock (this)
			{
				return _rejoinQueries;
			}
		}

		public Status GetStatus()
		{
			lock (this)
			{
				return _status;
			}
		}

		public Netki.Packet GetResponse()
		{
			lock (this)
			{
				return null;
			}
		}

		public void Done()
		{
			// hax to trigger response etc.
			AnonymousAuth("");
			_done = true;
		}

		private void Run()
		{
			// _status is written by this thread
			// _done is read by this thread
			_status = Status.CONNECTING;

			IPAddress addr = null;
			foreach (IPAddress e in Dns.GetHostAddresses(_host))
			{
				if (e.AddressFamily == AddressFamily.InterNetwork || e.AddressFamily == AddressFamily.InterNetworkV6)
				{
					addr = e;
				}
			}

			if (addr == null)
			{
				_status = Status.FAILED;
				return;
			}

			IPEndPoint remoteEP = new IPEndPoint(addr, NodeMaster.DEFAULT_CLIENT_PORT);
			using (Socket socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
			{
				try
				{
					socket.Connect(remoteEP);
					_status = Status.WAITING;

					lock (_requests)
					{
						foreach (Netki.Packet p in _requests)
						{
							Netki.Bitstream.Buffer buf = _pkg_handler.MakePacket(p);
							if (buf.bitsize != 0)
							{
								Console.WriteLine("Bitsize != 0!");
							}
							socket.Send(buf.buf, 0, (int)buf.bytesize, 0);
						}
						_requests.Clear();
						_socket = socket;
					}

					BufferedPacketDecoder dec = new BufferedPacketDecoder(65536, _pkg_handler);

					try
					{
						while (true)
						{
							byte[] readBuf = new byte[65536];
							int read = socket.Receive(readBuf);
							if (read <= 0)
							{
								if (_status == Status.WAITING)
									_status = Status.FAILED;
								break;
							}

							dec.OnStreamData(readBuf, 0, read, OnServerPacket);

							if (_done)
							{
								break;
							}
						}
					}
					catch (SocketException)
					{
						socket.Close();
					}
				}
				catch (SocketException er)
				{
					Console.WriteLine("Exception " + er.Message);
					_status = Status.FAILED;
				}
			}

			lock (_requests)
			{
				_requests.Clear();
				_socket = null;
			}

			if (!_done)
				_status = Status.FAILED;
		}
	}
}

