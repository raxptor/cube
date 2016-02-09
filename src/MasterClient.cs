﻿using System;
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

		public MasterClient(string host, ApplicationPacketHandler pkg_handler)
		{
			_host = host;
			_pkg_handler = pkg_handler;
			_status = Status.IDLE;
			_thread = new Thread(Run);
			_thread.Start();
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
						_socket.Send(buf.buf, 0, buf.bytesize, 0);
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
						_joinGameResponse = (Netki.MasterJoinGameResponse) packet.packet;
						break;
					default:
						break;
				}
			}
		}

		public Netki.MasterJoinGameResponse GetJoinGameResponse()
		{
			lock (this)
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
			lock (this)
			{
				_status = Status.DONE;
			}
		}

		private void Run()
		{
			lock (this)
			{
				_status = Status.CONNECTING;
			}

			IPHostEntry ipHostInfo = Dns.GetHostEntry(_host);
			IPAddress ipAddress = ipHostInfo.AddressList[0];
			IPEndPoint remoteEP = new IPEndPoint(ipAddress, NodeMaster.DEFAULT_CLIENT_PORT);
			Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			try
			{
				socket.Connect(remoteEP);
			
				lock (this)
				{
					_status = Status.WAITING;
				}

				lock (_requests)
				{
					foreach (Netki.Packet p in _requests)
					{
						Netki.Bitstream.Buffer buf = _pkg_handler.MakePacket(p);
						if (buf.bitsize != 0) {
							Console.WriteLine("Bitsize != 0!");
						}							
						socket.Send(buf.buf, 0, buf.bitsize, 0);
					}
					_requests.Clear();
					_socket = socket;
				}

				Netki.BufferedPacketDecoder dec = new Netki.BufferedPacketDecoder(65536, _pkg_handler);
				while (true)
				{
					byte[] readBuf = new byte[65536];
					int read = socket.Receive(readBuf);
					if (read <= 0)
					{
						lock (this)
						{
							if (_status == Status.WAITING)
								_status = Status.FAILED;
						}
						break;
					}

					dec.OnStreamData(readBuf, 0, read, OnServerPacket);

					lock (this)
					{
						if (_status == Status.DONE)
							break;
					}
				}

				socket.Close();
			}
			catch (SocketException)
			{

			}

			lock (_requests)
			{
				_requests.Clear();
				_socket = null;
			}

			lock (this)
			{
				if (_status != Status.DONE)
					_status = Status.FAILED;
			}
		}
	}
}

