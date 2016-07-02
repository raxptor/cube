using System.Collections.Generic;
using System.Threading;
using System;
using Netki;

namespace Cube
{
	public class GameClientConnection : Netki.StreamConnection
	{
		private Netki.ConnectionOutput _output;
		private NodeMaster _master;
		private string _id;
		private Netki.Packet _pending_request;
		private bool _disconnected;
		private BufferedPacketDecoder<CubePacketHolder> _decoder;
		DecodedPacket<CubePacketHolder> _pkt;

		public GameClientConnection(int connection_id, Netki.ConnectionOutput output, NodeMaster master)
		{
			_output = output;
			_decoder = new BufferedPacketDecoder<CubePacketHolder>(512, new CubePacketDecoder());
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

		public void OnAuthPacket(ref DecodedPacket<CubePacketHolder> decoded)
		{
			switch (decoded.type_id)
			{
				case Netki.MasterAuthenticateAnonymous.TYPE_ID:
					{
						Netki.MasterAuthenticateAnonymous anon = decoded.packet.MasterAuthenticateAnonymous;
						Console.WriteLine("Doing anonymous authentication [" + anon.Playername + "]");
						_id = "[" + anon.Playername +"]";
						break;
					}
				default:
					Console.WriteLine("Did not expect packet " + decoded.type_id + " in authentication state");
					break;
			}
		}

		public void OnPacket(ref DecodedPacket<CubePacketHolder> pkt)
		{
			switch (pkt.type_id)
			{
				case Netki.MasterJoinedGamesRequest.TYPE_ID:
				case Netki.MasterJoinConfigurationRequest.TYPE_ID:
				case Netki.MasterJoinGameRequest.TYPE_ID:

					var packet = pkt.GetPacket();
					lock (this)
					{
						if (_pending_request != null)
							return;
						_pending_request = packet;
					}
					//
					_master.OnClientRequest(packet, this);
					break;
				default:
					break;
			}
		}

		public bool IsClosed()
		{
			return _disconnected;
		}

		public void SendPacket<Pkt>(Pkt packet) where Pkt : Packet
		{
			switch (packet.GetTypeId())
			{
				case Netki.MasterJoinedGamesResponse.TYPE_ID:
				case Netki.MasterJoinGameResponse.TYPE_ID:
					lock (this)
					{
						_pending_request = null;
					}
					break;
			}

			Netki.Bitstream.Buffer buf = CubePacketsHandler.MakePacket(ref packet);
			if (buf.bitsize != 0) {
				Console.WriteLine ("bitsize != 0!");
			}
			_output.Send(buf.buf, 0, buf.bytesize);
		}

		public void OnStreamData(byte[] data, int offset, int length)
		{
			if (_id == null)
			{
				int decoded = _decoder.Decode(data, offset, length, ref _pkt);
				if (_pkt.type_id > 0)
				{
					OnAuthPacket(ref _pkt);
					if (_id != null)
					{
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

	public class ClientConnectionHandler : Netki.StreamConnectionHandler
	{
		NodeMaster _master;

		public ClientConnectionHandler(NodeMaster master)
		{
			_master = master;
		}

		public void OnStartup()
		{

		}

		public Netki.StreamConnection OnConnected(int connection_id, Netki.ConnectionOutput output)
		{
			return new GameClientConnection(connection_id, output, _master);
		}
	}
}