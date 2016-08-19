namespace Cube
{
	public interface ApplicationPacketHandler : PacketDecoder
	{
		 Netki.Bitstream.Buffer MakePacket(Netki.Packet packet);
	}
}