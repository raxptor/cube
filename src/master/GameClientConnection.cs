﻿using System.Collections.Generic;
using System.Threading;
using System;

namespace Cube
{
	public class GameClientConnection : StreamConnection
	{
		private ConnectionOutput _output;
		private BufferedPacketDecoder _decoder;
		private NodeMaster _master;
		private string _id;
		private Netki.Packet _pending_request;
		private bool _disconnected;
		private BufferedPacketDecoder _preAuthDecoder;
		private ApplicationPacketHandler _pkg_handler;

		public GameClientConnection(int connection_id, ApplicationPacketHandler pkg_handler, ConnectionOutput output, NodeMaster master)
		{
			_output = output;
			_pkg_handler = pkg_handler;
			_preAuthDecoder = new BufferedPacketDecoder(512, _pkg_handler);
			_master = master;
			_id = null;
		}

		public void OnDisconnected()
		{
			_disconnected = true;
			if (_id == null)
				return;
		}

		public string GetPlayerId()
		{
			return _id;
		}

		public void OnAuthPacket(Netki.DecodedPacket pkt)
		{
			switch (pkt.type_id)
			{
				case Netki.MasterAuthenticateAnonymous.TYPE_ID:
					{
						Netki.MasterAuthenticateAnonymous anon = (Netki.MasterAuthenticateAnonymous)pkt.packet;
						Console.WriteLine("Doing anonymous authentication [" + anon.Playername + "]");
						_id = "[" + anon.Playername +"]";
						break;
					}
				default:
					Console.WriteLine("Did not expect packet " + pkt.type_id + " in authentication state");
					break;
			}
		}

		public void OnPacket(Netki.DecodedPacket pkt)
		{
			switch (pkt.type_id)
			{
				case Netki.MasterJoinedGamesRequest.TYPE_ID:
				case Netki.MasterJoinConfigurationRequest.TYPE_ID:
				case Netki.MasterJoinGameRequest.TYPE_ID:

					lock (this)
					{
						if (_pending_request != null)
							return;
						_pending_request = pkt.packet;
					}
					//
					_master.OnClientRequest(pkt.packet, this);
					break;
				default:
					break;
			}
		}

		public bool IsClosed()
		{
			return _disconnected;
		}

		public void SendPacket(Netki.Packet packet)
		{
			switch (packet.type_id)
			{
				case Netki.MasterJoinedGamesResponse.TYPE_ID:
				case Netki.MasterJoinGameResponse.TYPE_ID:
					lock (this)
					{
						_pending_request = null;
					}
					break;
			}

            Netki.Bitstream.Buffer buf = _pkg_handler.MakePacket(packet);;
			if (buf.bitsize != 0) {
				Console.WriteLine ("bitsize != 0!");
			}
			_output.Send(buf.buf, 0, (int)buf.bytesize);
		}

		public void OnStreamData(byte[] data, int offset, int length)
		{
			if (_id == null)
			{
				Netki.DecodedPacket pkt;
				int decoded = _preAuthDecoder.Decode(data, offset, length, out pkt);
				if (pkt.type_id > 0)
				{
					OnAuthPacket(pkt);
					if (_id != null)
					{
						_preAuthDecoder = null;
						_decoder = new BufferedPacketDecoder(4096, _pkg_handler);
						OnStreamData(data, offset + decoded, length - decoded);
					}
				}
				return;
			}

			// 
			Console.WriteLine("[master:GameClientConnection] stream data " + length + " bytes");

			_decoder.OnStreamData(data, offset, length, OnPacket);
		}
	}

	public class ClientConnectionHandler : StreamConnectionHandler
	{
		NodeMaster _master;

		public ClientConnectionHandler(NodeMaster master)
		{
			_master = master;
		}

		public void OnStartup()
		{

		}

		public StreamConnection OnConnected(int connection_id, ConnectionOutput output)
		{
			return new GameClientConnection(connection_id, _master.GetPacketHandler(), output, _master);
		}
	}
}