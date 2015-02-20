using System.Collections.Generic;
using System.Threading;
using System;

namespace Cube
{
	public class GameNodeConnection : netki.StreamConnection
	{
		private netki.ConnectionOutput _output;
		private netki.BufferedPacketDecoder _decoder;
		private NodeMaster _master;
		private string _id;

		public GameNodeConnection(netki.ConnectionOutput output, NodeMaster master)
		{
			_output = output;
			_decoder = new netki.BufferedPacketDecoder(2*65536, master.GetPacketHandler());
			_master = master;
			_id = null;
		}

		public void OnDisconnected()
		{
			if (_id == null)
				return;
			_master.DisconnectInstance(_id);
		}

		public void OnPacket(netki.DecodedPacket pkt)
		{
			if (_id == null)
			{
				// only accept
				if (pkt.type_id == netki.GameNodeInfo.TYPE_ID)
				{
					netki.GameNodeInfo info = (netki.GameNodeInfo)pkt.packet;
					_id = info.NodeId;
					Console.WriteLine("node: identified as [" + _id + "] with address [" + info.NodeAddress + "]");
					_master.RegisterInstance(info, this);
				}
				else
				{
					Console.WriteLine("Invalid packet from unidentified node");
					return;
				}
			}
			else
			{
				_master.OnNodePacket(_id, pkt.packet);
			}
		}

		public void SendPacket(netki.Packet packet)
		{
			netki.Bitstream.Buffer buf = _master.GetPacketHandler().MakePacket(packet);
			_output.Send(buf.buf, 0, buf.bufsize);
		}

		public void OnStreamData(byte[] data, int offset, int length)
		{
			_decoder.OnStreamData(data, offset, length, OnPacket);
		}
	}

	public class NodeConnectionHandler : netki.StreamConnectionHandler
	{
		NodeMaster _master;

		public NodeConnectionHandler(NodeMaster master)
		{
			_master = master;
		}

		public void OnStartup()
		{

		}

		public netki.StreamConnection OnConnected(int connection_id, netki.ConnectionOutput output)
		{
			return new GameNodeConnection(output, _master);
		}
	}
}