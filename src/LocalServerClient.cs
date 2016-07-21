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
		private ApplicationPacketHandler _pkt_handler;
		private List<Datagram> _queue = new List<Datagram>();
		ulong _endpoint;
		static uint _endPointCounter = 0;
		private float _serverTickInterval;
		private float _serverTickAccum;

		public LocalServerClient(ApplicationPacketHandler handler, float serverTickInterval)
		{
			_endpoint = _endPointCounter++;
			_pkt_handler = handler;
			_serverTickInterval = serverTickInterval;
		}

		public bool Connect(IGameInstServer server, string playerId)
		{
			_server = server;
			return true;
		}

		ServerDatagram[] tmpDgrams = new ServerDatagram[16];
		public void Update(float deltaTime)
		{
			if (_serverTickInterval <= 0)
			{
				_server.Update();
				bool hasMore;
				do
				{
					uint count;
					hasMore = _server.GetOutgoingDatagrams(tmpDgrams, out count);
					for (uint i=0;i<count;i++)
					{
						Datagram d = new Datagram();
						d.Data = tmpDgrams[i].Data;
						d.Offset = tmpDgrams[i].Offset;
						d.Length = tmpDgrams[i].Length;
						_queue.Add(d);
					}
				}
				while (hasMore);
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
			lock (this)
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
			_server.HandleDatagrams(dgram, 1);
		}
	}
}

