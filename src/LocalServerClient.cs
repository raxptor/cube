using System;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;

namespace CCGMMO
{
	public class LocalServerClient : IGameInstClient
	{
		private IGameInstServer _server;
		private GameInstPlayer _player;
		private ApplicationPacketHandler _pkt_handler;
		private List<netki.Packet> _queue =new List<netki.Packet>();
		ulong _endpoint;
		static uint _endPointCounter = 0;

		class Player : GameInstPlayer
		{
		}

		public LocalServerClient(ApplicationPacketHandler handler)
		{
			_endpoint = _endPointCounter++;
			_pkt_handler = handler;
		}

		public bool Connect(IGameInstServer server, string playerId)
		{
			_player = new GameInstPlayer();
			_player.name = playerId;
			_server = server;
			bool succ = _server.ConnectPlayerStream(playerId, _player, delegate(netki.Packet packet) {
				_queue.Add(packet);
			});

			if (!succ)
			{
				return false;
			}

			_server.ConnectPlayerDatagram(playerId, _endpoint, delegate(netki.Packet packet) {
				_queue.Add(packet);
			});

			return true;
		}

		public void Update(float deltaTime)
		{
			_server.Update(deltaTime);
		}

		public netki.Packet[] ReadPackets()
		{
			lock(this)
			{
				netki.Packet[] pkts = _queue.ToArray();
				_queue.Clear();
				return pkts;
			}
		}

		public GameClientStatus GetStatus()
		{
			return GameClientStatus.READY;;
		}

		public void Send(netki.Packet packet, bool reliable)
		{
			if (reliable)
			{
				_server.PacketOnPlayer(_player, packet);
			}
			else
			{
				netki.Bitstream.Buffer buf = _pkt_handler.MakePacket(packet);
				_server.OnDatagram(buf.buf, 0, buf.bufsize, _endpoint);
			}
		}	
	}
}

