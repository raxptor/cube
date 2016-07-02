﻿using System.Collections.Generic;
using System.Threading;
using System;
using Netki;

namespace Cube
{
	public class GameNodeConnection : Netki.StreamConnection
	{
		private Netki.ConnectionOutput _output;
		private BufferedPacketDecoder<CubePacketHolder> _decoder;
		private NodeMaster _master;
		private string _id;

		public GameNodeConnection(Netki.ConnectionOutput output, NodeMaster master)
		{
			_output = output;
			_decoder = new BufferedPacketDecoder<CubePacketHolder>(2*65536, new CubePacketDecoder());
			_master = master;
			_id = null;
		}

		public void OnDisconnected()
		{
			if (_id == null)
				return;
			_master.DisconnectInstance(_id);
		}

		public void OnPacket(ref DecodedPacket<CubePacketHolder> pkt)
		{
			if (_id == null)
			{
				// only accept
				if (pkt.type_id == Netki.GameNodeInfo.TYPE_ID)
				{
					Netki.GameNodeInfo info = pkt.packet.GameNodeInfo;
					_id = info.NodeId;
					Console.WriteLine("node: identified as [" + _id + "]");
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
				_master.OnNodePacket(_id, ref pkt);
			}
		}

		public void SendPacket<Pkt>(ref Pkt packet) where Pkt : Packet
		{
			Netki.Bitstream.Buffer buf = CubePacketsHandler.MakePacket(ref packet);
			if (buf.bitsize == 0) {
				_output.Send(buf.buf, 0, buf.bytesize);
			} else {
				Console.WriteLine ("Trying to send packet with bitsize = " + buf.bitsize);
			}
		}

		public void OnStreamData(byte[] data, int offset, int length)
		{
			_decoder.OnStreamData(data, offset, length, OnPacket);
		}
	}

	public class NodeConnectionHandler : Netki.StreamConnectionHandler
	{
		NodeMaster _master;

		public NodeConnectionHandler(NodeMaster master)
		{
			_master = master;
		}

		public void OnStartup()
		{

		}

		public Netki.StreamConnection OnConnected(int connection_id, Netki.ConnectionOutput output)
		{
			return new GameNodeConnection(output, _master);
		}
	}
}