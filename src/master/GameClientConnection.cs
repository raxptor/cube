using System.Collections.Generic;
using System.Threading;
using System;

namespace Cube
{
	public class GameClientConnection : netki.StreamConnection
	{
		private netki.ConnectionOutput _output;
		private netki.BufferedPacketDecoder _decoder;
		private NodeMaster _master;
		private string _id;
		private netki.Packet _pending_request;
		private bool _disconnected;
		private netki.BufferedPacketDecoder _preAuthDecoder;
		private ApplicationPacketHandler _pkg_handler;

		public GameClientConnection(int connection_id, ApplicationPacketHandler pkg_handler, netki.ConnectionOutput output, NodeMaster master)
		{
			_output = output;
			_pkg_handler = pkg_handler;
			_preAuthDecoder = new netki.BufferedPacketDecoder(512, _pkg_handler);
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

		public void OnAuthPacket(netki.DecodedPacket pkt)
		{
			switch (pkt.type_id)
			{
				case netki.MasterAuthenticateAnonymous.TYPE_ID:
					{
						netki.MasterAuthenticateAnonymous anon = (netki.MasterAuthenticateAnonymous)pkt.packet;
						Console.WriteLine("Doing anonymous authentication [" + anon.Playername + "]");
						_id = "[" + anon.Playername +"]";
						break;
					}
				default:
					Console.WriteLine("Did not expect packet " + pkt.type_id + " in authentication state");
					break;
			}
		}

		public void OnPacket(netki.DecodedPacket pkt)
		{
			switch (pkt.type_id)
			{
				case netki.MasterJoinedGamesRequest.TYPE_ID:
				case netki.MasterJoinConfigurationRequest.TYPE_ID:
				case netki.MasterJoinGameRequest.TYPE_ID:

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

		public void SendPacket(netki.Packet packet)
		{
			switch (packet.type_id)
			{
				case netki.MasterJoinedGamesResponse.TYPE_ID:
				case netki.MasterJoinGameResponse.TYPE_ID:
					lock (this)
					{
						_pending_request = null;
					}
					break;
			}

            netki.Bitstream.Buffer buf = _pkg_handler.MakePacket(packet);;
			_output.Send(buf.buf, 0, buf.bufsize);
		}

		public void OnStreamData(byte[] data, int offset, int length)
		{
			if (_id == null)
			{
				netki.DecodedPacket pkt;
				int decoded = _preAuthDecoder.Decode(data, offset, length, out pkt);
				if (pkt.type_id > 0)
				{
					OnAuthPacket(pkt);
					if (_id != null)
					{
						_preAuthDecoder = null;
						_decoder = new netki.BufferedPacketDecoder(4096, _pkg_handler);
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

	public class ClientConnectionHandler : netki.StreamConnectionHandler
	{
		NodeMaster _master;

		public ClientConnectionHandler(NodeMaster master)
		{
			_master = master;
		}

		public void OnStartup()
		{

		}

		public netki.StreamConnection OnConnected(int connection_id, netki.ConnectionOutput output)
		{
			return new GameClientConnection(connection_id, _master.GetPacketHandler(), output, _master);
		}
	}
}