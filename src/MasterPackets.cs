﻿using System;
using Netki;

namespace Cube
{
    public class MasterPacketsHandler : Cube.ApplicationPacketHandler
    {
        public Bitstream.Buffer MakePacket(Netki.Packet packet)
        {
            Bitstream.Buffer buf = Bitstream.Buffer.Make(new byte[1024]);
            Bitstream.PutCompressedInt(buf, packet.type_id);
            Netki.CubePackets.Encode(packet, buf);
            buf.Flip();
            return buf;
        }

        public int Decode(byte[] data, int offset, int length, out DecodedPacket pkt)
        {
            Bitstream.Buffer buf = Bitstream.Buffer.Make(data);
            buf.bytesize = length;
            buf.bytepos = offset;
            int type_id = Bitstream.ReadCompressedInt(buf);
            if (Netki.CubePackets.Decode(buf, type_id, out pkt))
            {
                Bitstream.SyncByte(buf);
                return buf.bytepos - offset;
            }
            return 0;
        }
    }
}
