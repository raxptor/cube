using System;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;

namespace Cube
{
	public class RemoteGameClient : IGameInstClient
	{
		private GameClientStatus _status;
		private string _host, _token;
		private int _port;
		private Thread _thr;
		private Socket _socket;
		private List<Netki.Packet> _packets = new List<Netki.Packet>();
		private ApplicationPacketHandler _pkg_handler;

		public RemoteGameClient(string host, int port, string token, ApplicationPacketHandler handler)
		{
			_status = GameClientStatus.CONNECTING;
			_pkg_handler = handler;
			_host = host;
			_port = port;
			_token = token;
			_thr = new Thread(Run); 
			_thr.Start();
		}

		private static Netki.Packet[] s_Empty = new Netki.Packet[0] { };

		public Netki.Packet[] ReadPackets()
		{
			lock(this)
			{
				if (_packets.Count == 0)
				{
					return s_Empty;
				}
				Netki.Packet[] pkts = _packets.ToArray();
				_packets.Clear();
				return pkts;
			}
		}

		public GameClientStatus GetStatus()
		{
			lock(this)
			{
				return _status;
			}
		}

		public void Update(float deltaTime)
		{

		}

		public void Send(Netki.Packet packet, bool reliable)
		{
			lock(this)
			{
				try
				{
					if (packet.type_id == Netki.GameNodeRawDatagramWrapper.TYPE_ID)
                    {
                        Netki.GameNodeRawDatagramWrapper wrap = (Netki.GameNodeRawDatagramWrapper) packet;
						if (_udp_socket != null)
                            _udp_socket.Send(wrap.Data, wrap.Offset, wrap.Length, 0);
                        return;
					}

                    Netki.Bitstream.Buffer buf = _pkg_handler.MakePacket(packet);
                    if (!reliable)
                    {
                        if (_udp_socket != null)
                            _udp_socket.Send(buf.buf, 0, buf.bufsize, 0);
                    }
                    else if (_socket != null)
					{
						_socket.Send(buf.buf, 0, buf.bufsize, 0);
					}
				}
				catch(Exception)
				{
					_status = GameClientStatus.DISCONNECTED;
				}
			}
		}

		private byte[] _udp_buf = new byte[4096];
		private EndPoint _udp_remote = new IPEndPoint(IPAddress.Any, 0);
		private Socket _udp_socket = null; // set when connected

		private void OnUdpData(IAsyncResult res)
		{
			Socket s = (Socket)res.AsyncState;
			int bytes = s.EndReceiveFrom(res, ref _udp_remote);

			Netki.Bitstream.Buffer buf = Netki.Bitstream.Buffer.Make(_udp_buf);
			buf.bufsize = bytes;

			uint pkg = (uint)Netki.Bitstream.ReadBits(buf, 16);
			if (pkg == 0xffff)
			{
				Netki.GameNodeUnreliableAuthResponse resp = new Netki.GameNodeUnreliableAuthResponse();
				if (Netki.GameNodeUnreliableAuthResponse.ReadFromBitstream(buf, resp))
				{
					uint nowticks = (uint)DateTime.UtcNow.Ticks;
					uint diffticks = nowticks - resp.Time;
					Console.WriteLine("[gameclient] - udp established with lag = " + diffticks / 10000.0 + "ms");
					lock (this)
					{
						_udp_socket = s;
					}
				}
			}
			else if (_udp_socket != null)
			{
                // Receive as a wrapper.
                Netki.GameNodeRawDatagramWrapper wrap = new Netki.GameNodeRawDatagramWrapper();
                wrap.Data = new byte[bytes];
                wrap.Offset = 0;
                wrap.Length = bytes;
                Buffer.BlockCopy(_udp_buf, 0, wrap.Data, 0, bytes);
				lock (this)
				{
					_packets.Add(wrap);
				}
			}

			s.BeginReceiveFrom(_udp_buf, 0, _udp_buf.Length, 0, ref _udp_remote, OnUdpData, s);
		}

		private void UDPSetupThread(Netki.GameNodeSetupUnreliable setup)
		{
			try
			{
				Console.WriteLine("Attempting UDP setup " + setup.Host + ":" + setup.Port);
				Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

				// remote
				IPHostEntry ipHostInfo = Dns.GetHostEntry(setup.Host);
				IPAddress ipAddress = ipHostInfo.AddressList[0];
				IPEndPoint remoteEP = new IPEndPoint(ipAddress, (int)setup.Port);

				IPEndPoint localEP = new IPEndPoint(0, 0);
				s = new Socket(localEP.Address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
				s.Bind(localEP);
				s.Connect(remoteEP);

				s.BeginReceiveFrom(_udp_buf, 0, _udp_buf.Length, 0, ref _udp_remote, OnUdpData, s);

				int attempts = 30;
				while (--attempts > 0)
				{
					Netki.GameNodeUnreliableAuth auth = new Netki.GameNodeUnreliableAuth();
					auth.AuthId = setup.AuthId;
					auth.Key = setup.Key;
					auth.Time = (uint)DateTime.UtcNow.Ticks;
					Netki.Bitstream.Buffer buf = Netki.Bitstream.Buffer.Make(new byte[1024]);
					Netki.Bitstream.PutBits(buf, 16, 0xffff);
					Netki.GameNodeUnreliableAuth.WriteIntoBitstream(buf, auth);
					buf.Flip();
					s.Send(buf.buf, 0, buf.bufsize, 0);
					Thread.Sleep(100);

					lock (this)
					{
						if (_udp_socket != null)
						{
							Console.WriteLine("[gameclient] UDP Setup completed");
							return;
						}
					}
				}
			}
			catch (Exception)
			{

			}
		}

		private void Run()
		{
			try
			{
				lock (this)
				{
					_udp_socket = null;
				}

				Console.WriteLine("[rgc] connecting to " + _host + " port " + _port + " token:" + _token);

				IPHostEntry ipHostInfo = Dns.GetHostEntry(_host);
				IPAddress ipAddress = ipHostInfo.AddressList[0];
				IPEndPoint remoteEP = new IPEndPoint(ipAddress, _port);

				Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

				socket.Connect(remoteEP);

				Netki.GameNodeAuth auth =new Netki.GameNodeAuth();
				auth.Token = _token;
				Netki.Bitstream.Buffer buf = _pkg_handler.MakePacket(auth);
				socket.Send(buf.buf, 0, buf.bufsize, 0);

				lock (this)
				{
					_status = GameClientStatus.CONNECTED;
					_socket = socket;
				}

				Netki.BufferedPacketDecoder dec = new Netki.BufferedPacketDecoder(65536, _pkg_handler);

				while (true)
				{
					byte[] readBuf = new byte[65536];
					int read = socket.Receive(readBuf);

					lock (this)
					{
						if (read <= 0)
							_status = GameClientStatus.DISCONNECTED;		

						dec.OnStreamData(readBuf, 0, read, delegate(Netki.DecodedPacket packet) {

							if (packet.type_id == Netki.GameNodeSetupUnreliable.TYPE_ID)
							{
								Thread t2 = new Thread(delegate() {
									UDPSetupThread((Netki.GameNodeSetupUnreliable)packet.packet);
								});
								t2.Start();
							}

							if (_status == GameClientStatus.READY)
							{
								_packets.Add(packet.packet);
							}
							else if (_status == GameClientStatus.CONNECTED)
							{
								if (packet.type_id == Netki.GameNodeAuthResponse.TYPE_ID)
								{
									Netki.GameNodeAuthResponse resp = (Netki.GameNodeAuthResponse) packet.packet;
									Console.WriteLine("AuthSuccess = " + resp.AuthSuccess + " JoinSucces = " + resp.JoinSuccess);
									if (resp.AuthSuccess && resp.JoinSuccess)
									{
										_status = GameClientStatus.READY;
									}
									else
									{
										_status = GameClientStatus.DISCONNECTED;
									}
								}
								else
								{
									// Might come before the auth response
									_packets.Add(packet.packet);
								}
							}
						});

						if (_status == GameClientStatus.DISCONNECTED)
							break;
					}
				}
				socket.Close();
			}
			catch (SocketException)
			{
			}

			lock (this)
			{
				_socket = null;
				_status = GameClientStatus.DISCONNECTED;
			}
		}
	}
}

