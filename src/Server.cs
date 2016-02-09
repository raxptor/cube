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

	public interface IGameInstServer
	{
		bool CanPlayerReconnect(string playerId);
		bool OnDatagram(byte[] datagram, int offset, int length, ulong endpoint);
		ulong GetEndpoint();
		void Update(float deltaTime);
		Netki.GameNodeGameStatus GetStatus();
		bool CanShutdown();
		string GetVersionString(); // identification string for game/version
	}

	public enum GameClientStatus
	{
		IDLE,
		CONNECTING,
		CONNECTED,
		READY,
		DISCONNECTED
	}

	public struct Datagram
	{
		public byte[] data;
	}

	public interface IGameInstClient
	{
		GameClientStatus GetStatus();
		void Update(float deltaTime);
		void Send(Datagram datagram);
		Datagram[] ReadPackets();
	}
}



