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
		private List<Datagram> _queue = new List<Datagram>();
		ulong _endpoint;
		static uint _endPointCounter = 0;
        private float _serverTickInterval;
        private float _serverTickAccum;

        public LocalServerClient(float serverTickInterval)
		{
			_endpoint = _endPointCounter++;
            _serverTickInterval = serverTickInterval;
		}

		public bool Connect(IGameInstServer server, string playerId)
		{
			_server = server;
			return true;
		}

		public void Update(float deltaTime)
		{
            if (_serverTickInterval <= 0)
            {
                _server.Update();
                ServerDatagram[] dgrams = _server.GetOutgoingDatagrams();
                foreach (ServerDatagram dg in dgrams)
                {
                    Datagram d = new Datagram();
                    d.Data = dg.Data;
                    d.Offset = dg.Offset;
                    d.Length = dg.Length;
                    _queue.Add(d);
                }
            }
            else
            {
                _serverTickAccum += deltaTime;
                while (_serverTickAccum > _serverTickInterval)
                {
                    _server.Update();
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
            return GameClientStatus.CONNECTED;
		}

		public void Send(Datagram datagram)
		{
			ServerDatagram[] dgram = new ServerDatagram[1];
			dgram[0].Data = datagram.Data;
			dgram[0].Length = datagram.Length;
            dgram[0].Offset = datagram.Offset;
			dgram[0].Endpoint = _endpoint;
			_server.HandleDatagrams(dgram);
		}	
	}
}

