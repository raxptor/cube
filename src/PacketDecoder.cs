namespace Cube
{
	public interface ApplicationPacketHandler<HolderType> : Netki.PacketDecoder<HolderType>
	{
		Netki.Bitstream.Buffer MakePacket<Pkt>(Pkt packet) where Pkt : Netki.Packet;
	}
}