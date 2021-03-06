﻿using System;

namespace Cube
{
	public delegate void OnPacketDelegate(Netki.DecodedPacket packet);

	public class BufferedPacketDecoder : PacketDecoder
	{
		byte[] _data;
		PacketDecoder _decoder;
		int _parsepos, _readpos;
		bool _error = false;

		public BufferedPacketDecoder(int bufsize, PacketDecoder decoder)
		{
			_data = new byte[bufsize];
			_decoder = decoder;
			_parsepos = 0;
			_readpos = 0;
		}

		public int Decode(byte[] data, int offset, int length, out Netki.DecodedPacket pkt)
		{
			int ret;

			// When data exists in queue, add on and attempt decode.
			if (_readpos > 0)
			{
				if (!Save(data, offset, length))
				{
					_error = true;
					pkt.packet = null;
					pkt.type_id = -1;
					return length;
				}

				ret = DoDecode(_data, _parsepos, _readpos - _parsepos, out pkt);
				if (ret > 0)
					OnParsed(ret);
				return length;
			}

			// No data in queue; attempt decode directly in buffer
			ret = DoDecode(data, offset, length, out pkt);
			if (pkt.type_id < 0)
			{
				// No decode yet. Consume what it wants and store the rest.
				if (!Save(data, offset + ret, length - ret))
				{
					pkt.packet = null;
					pkt.type_id = -1;
					_error = true;
				}
				return length;
			}

			return ret;
		}

		public void OnParsed(int bytes)
		{
			_parsepos += bytes;
			if (_parsepos == _readpos)
			{
				_readpos = 0;
				_parsepos = 0;
			}
		}

		public void OnStreamData(byte[] data, int offset, int length, OnPacketDelegate handler)
		{
			while (true)
			{
				Netki.DecodedPacket pkt;
				int ret = Decode(data, offset, length, out pkt);

				offset += ret;
				length -= ret;
				if (pkt.type_id < 0)
					break;
				handler(pkt);
			}
		}

		public bool HasError()
		{
			return _error;
		}

		public bool Save(byte[] data, int offset, int length)
		{
			if (_readpos + length > _data.Length)
				return false;

			for (int i = 0; i < length; i++)
			{
				_data[_readpos + i] = data[offset + i];
			}
			_readpos += length;
			return true;
		}

		public int DoDecode(byte[] data, int offset, int length, out Netki.DecodedPacket pkt)
		{
			return _decoder.Decode(data, offset, length, out pkt);
		}
	}
}
