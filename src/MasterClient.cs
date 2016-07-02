﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;
using Netki;

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

		bool _hasJoinGameResponse;
		Netki.MasterJoinGameResponse _joinGameResponse;

		public MasterClient(string host)
		{
			_host = host;
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

		public bool QueueRequest<Pkt>(Pkt packet) where Pkt : Netki.Packet
		{
			lock (_requests)
			{
				if (_socket != null)
				{
					Netki.Bitstream.Buffer buf = CubePacketsHandler.MakePacket<Pkt>(ref packet);
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

		public void OnServerPacket(ref DecodedPacket<CubePacketHolder> packet)
		{
			lock (this)
			{
				switch (packet.type_id)
				{
					case Netki.MasterJoinedGamesResponse.TYPE_ID:
						{
							var resp = packet.packet.MasterJoinedGamesResponse;
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
						_joinGameResponse = packet.packet.MasterJoinGameResponse;
						_hasJoinGameResponse = true;
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
				_hasJoinGameResponse = false;
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
                if (_socket != null)
                {
                    _socket.Close();
                    _socket = null;
                }
			}
		}

		private void Run()
		{
			lock (this)
			{
				_status = Status.CONNECTING;
			}

            IPAddress addr = null;
            foreach (IPAddress e in Dns.GetHostEntry(_host).AddressList)
            {             
                if (e.AddressFamily == AddressFamily.InterNetwork)
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
            Socket socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
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
						Netki.Bitstream.Buffer buf = CubePacketsHandler.MakePacket(p);
						if (buf.bitsize != 0) {
							Console.WriteLine("Bitsize != 0!");
						}							
						socket.Send(buf.buf, 0, buf.bytesize, 0);
					}
					_requests.Clear();
					_socket = socket;
				}

				BufferedPacketDecoder<CubePacketHolder> dec = new BufferedPacketDecoder<CubePacketHolder>(65536, new CubePacketDecoder());

                try 
                {
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
                            {
                                socket.Close();
                                _socket = null;
    							break;
                            }
    					}
                    }                    
				}
                catch (SocketException)
                {
                    _socket = null;
                }

				socket.Close();
			}
			catch (SocketException er)
			{
                Console.WriteLine("Exception " + er.Message);
                _status = Status.FAILED;
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

