using System;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;

namespace Cube
{
	public class LocalServerClient : IGameInstClient
	{
		private IGameInstServer _server;
		private GameInstPlayer _player;
		private ApplicationPacketHandler _pkt_handler;
		private List<Datagram> _queue = new List<Datagram>();
		ulong _endpoint;
		static uint _endPointCounter = 0;
        private float _serverTickInterval;
        private float _serverTickAccum;

		class Player : GameInstPlayer
		{
		}

        public LocalServerClient(ApplicationPacketHandler handler, float serverTickInterval)
		{
			_endpoint = _endPointCounter++;
			_pkt_handler = handler;
            _serverTickInterval = serverTickInterval;
		}

		public bool Connect(IGameInstServer server, string playerId)
		{
			_player = new GameInstPlayer();
			_player.name = playerId;
			_server = server;
			return true;
		}

		public void Update(float deltaTime)
		{
            if (_serverTickInterval <= 0)
            {
                _server.Update(deltaTime);
            }
            else
            {
                _serverTickAccum += deltaTime;
                while (_serverTickAccum > _serverTickInterval)
                {
                    _server.Update(_serverTickInterval);
                    _serverTickAccum -= _serverTickInterval;
                }
            }
		}

		public Datagram[] ReadPackets()
		{
			lock(this)
			{
				Datagram[] pkts = _queue.ToArray();
				_queue.Clear();
				return pkts;
			}
		}

		public GameClientStatus GetStatus()
		{
			return GameClientStatus.READY;;
		}

		public void Send(Datagram datagram)
		{
			_server.OnDatagram(datagram.data, 0, datagram.data.Length, _endpoint);
		}	
	}
}

