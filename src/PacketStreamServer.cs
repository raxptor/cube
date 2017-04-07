using System;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;

namespace Cube
{
	public class PacketStreamServer
	{
		private StreamConnectionHandler _handler;
		private Socket _listener4;
		private Socket _listener6;

		class Connection : ConnectionOutput
		{
			public Socket socket;
			public StreamConnection conn;
			public byte[] recvbuf;

			~Connection()
			{

			}

			public void Send(byte[] data, int offset, int length)
			{
				try
				{
					if (socket.Connected)
					{
						socket.Send(data, offset, length, 0);
					}
				}
				catch (System.Net.Sockets.SocketException)
				{
				}
				catch (System.ObjectDisposedException)
				{
				}
			}
		};

		private List<int> _free_connections = new List<int>();
		private Connection[] _connections;

		public PacketStreamServer(StreamConnectionHandler handler)
		{
			_handler = handler;
		}

		private void InternalOnDisconnected(int connection_id)
		{
			Console.WriteLine("Connection disconnected (" + connection_id + ")");
			Connection conn = _connections[connection_id];
			conn.conn.OnDisconnected();
			_connections[connection_id] = null;

			lock (_free_connections)
			{
				_free_connections.Add(connection_id);
			}

			try
			{
				conn.socket.Close();
			}
			catch (Exception)
			{

			}
		}


		public void OnAsyncReceive(IAsyncResult result)
		{
			int connection_id = (int)result.AsyncState;
			Connection conn = _connections[connection_id];

			int ret;
			try
			{
				ret = conn.socket.EndReceive(result);
			}
			catch (Exception)
			{
				InternalOnDisconnected(connection_id);
				return;
			}

			if (ret <= 0)
			{
				InternalOnDisconnected(connection_id);
			}
			else
			{
				System.Random r = new System.Random();
				int rp = 0;
				while (rp < ret)
				{
					int amt = r.Next() % (ret - rp + 1);
					amt = ret - rp;
					if (amt > 0)
					{
						conn.conn.OnStreamData(conn.recvbuf, rp, amt);
						rp += amt;
					}
				}
				try
				{
					conn.socket.BeginReceive(conn.recvbuf, 0, conn.recvbuf.Length, 0, OnAsyncReceive, connection_id);
				}
				catch (Exception)
				{
					InternalOnDisconnected(connection_id);
				}
			}
		}

		public void OnAsyncAccepted4(IAsyncResult result)
		{
			Socket nsock = _listener4.EndAccept(result);

			lock (_free_connections)
			{
				// Occupy new slot.
				int pos = _free_connections.Count - 1;
				if (pos >= 0)
				{
					int connection_id = _free_connections[pos];
					_free_connections.RemoveAt(pos);

					Connection c = new Connection();
					c.socket = nsock;
					c.recvbuf = new byte[4096];
					c.conn = _handler.OnConnected(connection_id, c);
					_connections[connection_id] = c;

					nsock.ReceiveTimeout = 5*60*1000; // 5 min
					nsock.BeginReceive(c.recvbuf, 0, c.recvbuf.Length, 0, OnAsyncReceive, connection_id);
				}
				else
				{
					Console.WriteLine("Dropping connection because i am full");
					nsock.Close();
				}
			}

			_listener4.BeginAccept(OnAsyncAccepted4, _listener4);
		}


		public void OnAsyncAccepted6(IAsyncResult result)
		{
			Socket nsock = _listener6.EndAccept(result);

			lock (_free_connections)
			{
				// Occupy new slot.
				int pos = _free_connections.Count - 1;
				if (pos >= 0)
				{
					int connection_id = _free_connections[pos];
					_free_connections.RemoveAt(pos);

					Connection c = new Connection();
					c.socket = nsock;
					c.recvbuf = new byte[4096];
					c.conn = _handler.OnConnected(connection_id, c);
					_connections[connection_id] = c;

					nsock.ReceiveTimeout = 5 * 60 * 1000; // 5 min
					nsock.BeginReceive(c.recvbuf, 0, c.recvbuf.Length, 0, OnAsyncReceive, connection_id);
				}
				else
				{
					Console.WriteLine("Dropping connection because i am full");
					nsock.Close();
				}
			}

			_listener6.BeginAccept(OnAsyncAccepted6, _listener6);
		}

		public int GetNumConnections()
		{
			lock (_free_connections)
			{
				return _connections.Length - _free_connections.Count;
			}
		}

		public int GetPort()
		{
			return ((IPEndPoint)_listener4.LocalEndPoint).Port;
		}

		// returns port.
		public void Start(int port, int max_connections = 100)
		{
			_connections = new Connection[max_connections];
			for (int i = 0; i < max_connections; i++)
				_free_connections.Add(max_connections - i - 1);

			IPEndPoint localEP4 = new IPEndPoint(IPAddress.Any, port);
			_listener4 = new Socket(localEP4.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
			_listener4.Bind(localEP4);
			_listener4.Listen(100);
			_listener4.BeginAccept(OnAsyncAccepted4, _listener4);

			IPEndPoint localEP6 = new IPEndPoint(IPAddress.IPv6Any, port);
			_listener6 = new Socket(localEP6.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
			_listener6.Bind(localEP6);
			_listener6.Listen(100);
			_listener6.BeginAccept(OnAsyncAccepted6, _listener4);
		}
	}
}
