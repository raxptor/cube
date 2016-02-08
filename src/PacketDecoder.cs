namespace Cube
{
	public interface ApplicationPacketHandler : Netki.PacketDecoder
	{
		 Netki.Bitstream.Buffer MakePacket(Netki.Packet packet);
	}
}