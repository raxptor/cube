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
		private List<Datagram> _packets = new List<Datagram>();
		private ApplicationPacketHandler _pkg_handler;

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

		}

		public void Send(Datagram dgram)
		{
			lock(this)
			{
				try
				{
	                if (_udp_socket != null)
					{
						_udp_socket.Send(dgram.data, 0, dgram.data.Length, 0);
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

			byte[] data = new byte[bytes];
			Buffer.BlockCopy(_udp_buf, 0, data, 0, bytes);

			Datagram d = new Datagram();
			d.data = data;
			_packets.Add(d);

			s.BeginReceiveFrom(_udp_buf, 0, _udp_buf.Length, 0, ref _udp_remote, OnUdpData, s);
		}
	}

		/*
		 * 
		 *				Console.WriteLine("Attempting UDP setup " + setup.Host + ":" + setup.Port);
				Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

				// remote

*/
}

