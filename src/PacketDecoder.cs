namespace Cube
{
	public interface PacketDecoder
	{
		int Decode(byte[] data, int offset, int length, out Netki.DecodedPacket pkt);
	}

	public interface ApplicationPacketHandler : PacketDecoder
	{
		 Netki.Bitstream.Buffer MakePacket(Netki.Packet packet);
	}
}