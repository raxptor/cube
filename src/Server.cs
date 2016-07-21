using System;

namespace Cube
{
	public delegate void PacketExchangeDelegate(Netki.Packet packet);
	public delegate byte[] PacketEncodeDelegate(Netki.Packet packet);
	public delegate void DatagramExchangeDelegate(byte[] buf, int offset, int length);

	public class GameInstPlayer
	{
		public string name;
	}

	public struct ServerDatagram
	{		
		public byte[] Data;
		public uint Offset, Length;
		public ulong Endpoint;
	}
        
    public delegate void OnTokenConsumed(string Which);

	public interface IGameInstServer
	{
		bool CanPlayerReconnect(string playerId);
        void GiveKnockTocken(string token, OnTokenConsumed consumed);
		void HandleDatagrams(ServerDatagram[] datagrams, uint count);
		bool GetOutgoingDatagrams(ServerDatagram[] output, out uint count);
		void Update();
		Netki.GameNodeGameStatus GetStatus();
		bool CanShutdown();
		string GetVersionString();
	}

	public struct Datagram
	{
		public byte[] Data;
        public uint Offset, Length;
	}

	public interface IGameInstClient
	{
		GameClientStatus GetStatus();
		void Update(float deltaTime);
		void Send(Datagram datagram);
		Datagram[] ReadPackets();
	}
}
