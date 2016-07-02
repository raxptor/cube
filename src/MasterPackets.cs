using System;
using Netki;

namespace Cube
{
	public static class CubePacketsHandler
    {
		public static Bitstream.Buffer MakePacket<Pkt>(ref Pkt packet) where Pkt : Packet
		{
            Bitstream.Buffer buf = Bitstream.Buffer.Make(new byte[1024]);
			Bitstream.PutCompressedInt(buf, packet.GetTypeId());
			packet.Write(buf);
            buf.Flip();
            return buf;
        }

		public static Bitstream.Buffer MakePacket(Packet packet)
		{
			Bitstream.Buffer buf = Bitstream.Buffer.Make(new byte[1024]);
			Bitstream.PutCompressedInt(buf, packet.GetTypeId());
			packet.Write(buf);
			buf.Flip();
			return buf;
		}

		public static int Decode(byte[] data, int offset, int length, ref DecodedPacket<CubePacketHolder> pkt)
        {
            Bitstream.Buffer buf = Bitstream.Buffer.Make(data);
            buf.bytesize = offset + length;
            buf.bytepos = offset;
            int type_id = Bitstream.ReadCompressedInt(buf);
            if (Netki.CubePackets.Decode(buf, type_id, ref pkt))
            {
                Bitstream.SyncByte(buf);
                return buf.bytepos - offset;
            }
            return 0;
        }
    }

	public class CubePacketDecoder : PacketDecoder<CubePacketHolder>
	{
		public int Decode(byte[] data, int offset, int length, ref DecodedPacket<CubePacketHolder> pkt)
		{
			return -1;
		}
	}
}
