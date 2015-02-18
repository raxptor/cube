using System;

namespace CCGMMO
{
	public delegate void PacketExchangeDelegate(netki.Packet packet);
	public delegate byte[] PacketEncodeDelegate(netki.Packet packet);
	public delegate void DatagramExchangeDelegate(byte[] buf, int offset, int length);

	public class GameInstPlayer
	{
		public string name;
	}

	// 
	public interface IGameInstServer
	{
		bool CanPlayerConnect(string playerId);
		bool ConnectPlayerStream(string playerId, GameInstPlayer player, PacketExchangeDelegate _send_to_me);
		void ConnectPlayerDatagram(string playerId, ulong endpoint, PacketExchangeDelegate _send_to_me);
		bool OnDatagram(byte[] datagram, int offset, int length, ulong endpoint);
		void PacketOnPlayer(GameInstPlayer player, netki.Packet packet);
		void DisconnectPlayer(GameInstPlayer player);

		void Update(float deltaTime);

		//
		netki.GameNodeGameStatus GetStatus();
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
		void Send(netki.Packet packet, bool reliable);
		netki.Packet[] ReadPackets();
	}
}



