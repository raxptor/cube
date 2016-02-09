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
		private List<Datagram> _packets = new List<Datagram>();
		private ApplicationPacketHandler _pkg_handler;

		private byte[] _udp_buf = new byte[4096];
		private EndPoint _udp_remote = new IPEndPoint(IPAddress.Any, 0);
		private Socket _socket = null; // set when connected
		private DateTime _lastRecv = DateTime.Now.AddDays(-10);

		public RemoteGameClient(string host, int port, string token, ApplicationPacketHandler handler)
		{
			_status = GameClientStatus.CONNECTING;
			_pkg_handler = handler;
			_host = host;
			_port = port;
			_token = token;

			IPHostEntry ipHostInfo = Dns.GetHostEntry(_host);
			IPAddress ipAddress = ipHostInfo.AddressList[0];
			IPEndPoint remoteEP = new IPEndPoint(ipAddress, (int)_port);
			IPEndPoint localEP = new IPEndPoint(0, 0);

			_socket = new Socket(localEP.Address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
			_socket.Bind(localEP);
			_socket.Connect(remoteEP);
			_socket.BeginReceiveFrom(_udp_buf, 0, _udp_buf.Length, 0, ref _udp_remote, OnUdpData, _socket);
		}

		private static Datagram[] s_Empty = new Datagram[0] { };

		public Datagram[] ReadPackets()
		{
			lock(this)
			{
				if (_packets.Count == 0)
				{
					return s_Empty;
				}
				Datagram[] pkts = _packets.ToArray();
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
			if (_lastRecv != null)
			{
				double sincePacket = (DateTime.Now - _lastRecv).TotalSeconds;
				if (sincePacket > 10.0f) 
				{
					lock (this)
					{
						Console.WriteLine("Connection to server timed out");
						_status = GameClientStatus.DISCONNECTED;
						_socket.Close();
					}
				}
			}

		}

		public void Send(Datagram dgram)
		{
			lock(this)
			{
				try
				{
                    _socket.Send(dgram.Data, (int)dgram.Offset, (int)dgram.Length, 0);
				}
				catch(Exception)
				{
					_status = GameClientStatus.DISCONNECTED;
				}
			}
		}
			
		private void OnUdpData(IAsyncResult res)
		{
			Socket s = (Socket)res.AsyncState;
			int bytes = s.EndReceiveFrom(res, ref _udp_remote);

			byte[] data = new byte[bytes];
			Buffer.BlockCopy(_udp_buf, 0, data, 0, bytes);

			lock (this)
			{
				Datagram d = new Datagram ();
				d.Data = data;
                d.Offset = 0;
                d.Length = (uint)bytes;
				_packets.Add(d);
				_lastRecv = DateTime.Now;
				_status = GameClientStatus.CONNECTED;
			}

			s.BeginReceiveFrom(_udp_buf, 0, _udp_buf.Length, 0, ref _udp_remote, OnUdpData, s);
		}
	}
}

