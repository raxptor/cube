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

	// 
	public interface IGameInstServer
	{
		bool CanPlayerReconnect(string playerId);
		bool ConnectPlayerStream(string playerId, GameInstPlayer player, PacketExchangeDelegate _send_to_me);
		void ConnectPlayerDatagram(string playerId, ulong endpoint, PacketExchangeDelegate _send_to_me);
		bool OnDatagram(byte[] datagram, int offset, int length, ulong endpoint);
		void PacketOnPlayer(GameInstPlayer player, Netki.Packet packet);
		void DisconnectPlayer(GameInstPlayer player);

		void Update(float deltaTime);

		//
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

	public interface IGameInstClient
	{
		GameClientStatus GetStatus();
		void Update(float deltaTime);
		void Send(Netki.Packet packet, bool reliable);
		Netki.Packet[] ReadPackets();
	}
}



