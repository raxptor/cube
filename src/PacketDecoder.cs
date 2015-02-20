namespace Cube
{
	public interface ApplicationPacketHandler : netki.PacketDecoder
	{
		 netki.Bitstream.Buffer MakePacket(netki.Packet packet);
	}
}