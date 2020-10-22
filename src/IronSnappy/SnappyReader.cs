using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace IronSnappy
{
   class SnappyReader : Stream
   {
      const int DecodeErrCodeCorrupt = 1;
      const int DecodeErrCodeUnsupportedLiteralLength = 2;
      const string ErrCorrupt = "corrupt input";
      const string ErrUnsupported = "unsupported input";

      private static readonly ArrayPool<byte> BytePool = ArrayPool<byte>.Shared;
      private readonly Stream _parent;
      private int _i;
      private int _j;
      private bool _readHeader;
      private readonly byte[] _buf;
      private readonly byte[] _decoded;

      public SnappyReader(Stream parent)
      {
         _parent = parent;
         _buf = Snappy.BytePool.Rent(Snappy.MaxEncodedLenOfMaxBlockSize + Snappy.ChecksumSize);
         _decoded = Snappy.BytePool.Rent(Snappy.MaxBlockSize);
      }

      public override bool CanRead => true;

      public override bool CanSeek => false;

      public override bool CanWrite => false;

      public override long Length => _parent.Length;

      public override long Position { get => _parent.Position; set => throw new NotSupportedException(); }

      public override void Flush() { }

      public override int Read(byte[] buffer, int offset, int count)
      {
         while(true)
         {
            if(_i < _j)
            {
               // Number of bytes to copy.
               int n = Math.Min(_j - _i, count);
               _decoded.AsSpan(_i, n).CopyTo(buffer.AsSpan(offset, n));
               _i += n;
               return n;
            }

            int chunkHeaderBytes = _parent.Read(_buf, 0, 4);
            if(chunkHeaderBytes == 0 && _readHeader == true)
            {
               // end of file reached.
               return 0;
            }
            if(4 != chunkHeaderBytes)
            {
               throw new IOException("corrupt input");
            }

            byte chunkType = _buf[0];
            if(!_readHeader)
            {
               if(chunkType != Snappy.ChunkTypeStreamIdentifier)
               {
                  throw new IOException("corrupt input");
               }
               _readHeader = true;
            }

            int chunkLen = (int)_buf[1] | ((int)(_buf[2]) << 8) | ((int)(_buf[3]) << 16);
            if(chunkLen > _buf.Length)
            {
               throw new IOException("unsupported input");
            }

            // The chunk types are specified at
            // https://github.com/google/snappy/blob/master/framing_format.txt
            switch(chunkType)
            {
               case Snappy.ChunkTypeCompressedData:
               {
                  // Section 4.2. Compressed data (chunk type 0x00).
                  if(chunkLen < Snappy.ChecksumSize)
                  {
                     throw new IOException(ErrCorrupt);
                  }
                  Span<byte> buf = _buf.AsSpan().Slice(0, chunkLen);

                  if(chunkLen != Spans.Read(_parent, buf))
                  {
                     throw new IOException(ErrCorrupt);
                  }

                  uint checksum = (uint)(buf[0]) | ((uint)(buf[1]) << 8) | ((uint)(buf[2]) << 16) | ((uint)(buf[3]) << 24);

                  buf = buf.Slice(Snappy.ChecksumSize);

                  int n = DecodedLen(buf);
                  if(n > _decoded.Length)
                  {
                     throw new IOException(ErrCorrupt);
                  }

                  Decode(_decoded, buf);

                  if(Crc32.Compute(_decoded.AsSpan(0, n)) != checksum)
                  {
                     throw new IOException(ErrCorrupt);
                  }

                  _i = 0;
                  _j = n;
               }

               continue;


               case Snappy.ChunkTypeUncompressedData:
               {
                  // Section 4.3. Uncompressed data (chunk type 0x01).
                  if(chunkLen < Snappy.ChecksumSize)
                  {
                     throw new IOException(ErrCorrupt);
                  }

                  Span<byte> buf = _buf.AsSpan(0, Snappy.ChecksumSize);

                  Spans.Read(_parent, buf);

                  uint checksum = (uint)(buf[0]) | (uint)(buf[1]) << 8 | (uint)(buf[2]) << 16 | (uint)(buf[3]) << 24;
                  // Read directly into r.decoded instead of via r.buf.
                  int n = chunkLen - Snappy.ChecksumSize;

                  if(n > _decoded.Length)
                  {
                     throw new IOException(ErrCorrupt);
                  }

                  Spans.Read(_parent, _decoded.AsSpan(0, n));

                  if(Crc32.Compute(_decoded.AsSpan(0, n)) != checksum)
                  {
                     throw new IOException(ErrCorrupt);
                  }
                  _i = 0;
                  _j = n;
               }
               continue;


               case Snappy.ChunkTypeStreamIdentifier:
               {
                  // Section 4.1. Stream identifier (chunk type 0xff).
                  if(chunkLen != Snappy.MagicBody.Length)
                  {
                     throw new IOException(ErrCorrupt);
                  }

                  Spans.Read(_parent, _buf.AsSpan(0, Snappy.MagicBody.Length));

                  for(int i = 0; i < Snappy.MagicBody.Length; i++)
                  {
                     if(_buf[i] != Snappy.MagicBody[i])
                     {
                        throw new IOException(ErrCorrupt);
                     }
                  }
               }
               continue;

            }

            if(chunkType <= 0x7f)
            {
               // Section 4.5. Reserved unskippable chunks (chunk types 0x02-0x7f).
               throw new IOException(ErrUnsupported);
            }

            // Section 4.4 Padding (chunk type 0xfe).
            // Section 4.6. Reserved skippable chunks (chunk types 0x80-0xfd).
            Spans.Read(_parent, _buf.AsSpan(0, chunkLen));
         }
      }

      public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();
      public override void SetLength(long value) => throw new NotImplementedException();
      public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

      // Uvarint decodes a uint64 from buf and returns that value and the
      // number of bytes read (> 0). If an error occurred, the value is 0
      // and the number of bytes n is <= 0 meaning:
      //
      // 	n == 0: buf too small
      // 	n  < 0: value larger than 64 bits (overflow)
      // 	        and -n is the number of bytes read
      //
      static ulong Uvarint(ReadOnlySpan<byte> buf, out int bytesRead)
      {
         ulong x = 0;
         int s = 0;

         for(int i = 0; i < buf.Length; i++)
         {
            byte b = buf[i];

            if(b < 0x80)
            {
               if(i > 9 || (i == 9 && b > 1))
               {
                  bytesRead = -(i + 1);
                  return 0;

               }

               bytesRead = i + 1;
               return (ulong)((int)x | (int)b << s);

            }
            x |= (ulong)(b & 0x7f) << s;
            s += 7;
         }

         bytesRead = 0;
         return 0;
      }

      // DecodedLen returns the length of the decoded block.
      public static int DecodedLen(ReadOnlySpan<byte> src)
      {
         DecodedLen(src, out int blockLen, out _);

         return blockLen;
      }

      // decodedLen returns the length of the decoded block and the number of bytes
      // that the length header occupied.
      static void DecodedLen(ReadOnlySpan<byte> src, out int blockLen, out int headerLen)
      {
         ulong v = Uvarint(src, out int n);

         if(n <= 0 || v > 0xffffffff)
         {
            throw new IOException("corrupt input");
         }

         const uint wordSize = (uint)32 << ((~0 >> 32) & 1);
         if(wordSize == 32 && v > 0x7fffffff)
         {
            throw new IOException("decoded block is too large");
         }

         blockLen = (int)v;
         headerLen = n;
      }

      static int DecodeInternal(Span<byte> dst, ReadOnlySpan<byte> src)
      {
         int d = 0, s = 0, offset = 0, length = 0;

         while(s < src.Length)
         {
            switch(src[s] & 0x03)
            {
               case Snappy.TagLiteral:
                  uint x = (uint)(src[s] >> 2);

                  if(x < 60)
                  {
                     s++;
                  }
                  else if(x == 60)
                  {

                     s += 2;
                     if(s > src.Length)
                     {
                        // The uint conversions catch overflow from the previous line.
                        return DecodeErrCodeCorrupt;
                     }
                     x = (uint)(src[s - 1]);
                  }
                  else if(x == 61)
                  {
                     s += 3;
                     if(s > src.Length)
                     {
                        // The uint conversions catch overflow from the previous line.
                        return DecodeErrCodeCorrupt;
                     }
                     x = (uint)(src[s - 2]) | ((uint)(src[s - 1]) << 8);
                  }
                  else if(x == 62)
                  {
                     s += 4;
                     if(s > src.Length)
                     {
                        // The uint conversions catch overflow from the previous line.
                        return DecodeErrCodeCorrupt;
                     }
                     x = (uint)(src[s - 3]) | (uint)(src[s - 2]) << 8 | (uint)(src[s - 1]) << 16;
                  }
                  else if(x == 63)
                  {
                     s += 5;
                     if(s > src.Length)
                     {
                        // The uint conversions catch overflow from the previous line.
                        return DecodeErrCodeCorrupt;
                     }
                     x = (uint)(src[s - 4]) | ((uint)(src[s - 3]) << 8) | ((uint)(src[s - 2]) << 16) | ((uint)(src[s - 1]) << 24);
                  }

                  length = (int)x + 1;

                  if(length <= 0)
                  {
                     return DecodeErrCodeUnsupportedLiteralLength;
                  }

                  if(length > dst.Length - d || length > src.Length - s)
                  {
                     return DecodeErrCodeCorrupt;
                  }

                  src.Slice(s, length).CopyTo(dst.Slice(d));

                  d += length;
                  s += length;

                  continue;

               case Snappy.TagCopy1:
                  s += 2;
                  if(s > src.Length)
                  {
                     // The uint conversions catch overflow from the previous line.
                     return DecodeErrCodeCorrupt;
                  }
                  //4 + ((17 >> 2) & 0x7)
                  length = 4 + ((src[s - 2] >> 2) & 0x7);
                  //(33 & 0xe0) << 3 | 0
                  offset = ((src[s - 2] & 0xe0) << 3) | src[s-1];

                  break;

               case Snappy.TagCopy2:
                  s += 3;
                  if((int)s > (int)src.Length)
                  {
                     // The uint conversions catch overflow from the previous line.
                     return DecodeErrCodeCorrupt;
                  }
                  length = 1 + (src[s - 3] >> 2);
                  offset = (int)(src[s - 2] | ((uint)(src[s - 1]) << 8));

                  break;

               case Snappy.TagCopy4:
                  s += 5;
                  if((int)(s) > (int)(src.Length))
                  {
                     // The uint conversions catch overflow from the previous line.
                     return DecodeErrCodeCorrupt;
                  }
                  length = 1 + (src[s - 5] >> 2);
                  offset = (int)((uint)(src[s - 4]) | (uint)(src[s - 3]) << 8 | (uint)(src[s - 2]) << 16 | (uint)(src[s - 1]) << 24);
                  break;
            }

            if(offset <= 0 || d < offset || length > dst.Length - d)
            {
               return DecodeErrCodeCorrupt;
            }

            // Copy from an earlier sub-slice of dst to a later sub-slice.
            // If no overlap, use the built-in copy:
            if(offset >= length)
            {
               dst.Slice(d - offset, length).CopyTo(dst.Slice(d, length));

               d += length;
               continue;
            }

            // Unlike the built-in copy function, this byte-by-byte copy always runs
            // forwards, even if the slices overlap. Conceptually, this is:
            //
            // d += forwardCopy(dst[d:d+length], dst[d-offset:])
            //
            // We align the slices into a and b and show the compiler they are the same size.
            // This allows the loop to run without bounds checks.
            Span<byte> a = dst.Slice(d, length);
            Span<byte> b = dst.Slice(d - offset);
            b = b.Slice(0, a.Length);
            for(int i = 0; i < a.Length; i++)
            {
               a[i] = b[i];
            }
            d += length;
         }

         if(d != dst.Length)
         {
            return DecodeErrCodeCorrupt;

         }
         return 0;
      }

      // Decode returns the decoded form of src. The returned slice may be a sub-
      // slice of dst if dst was large enough to hold the entire decoded block.
      // Otherwise, a newly allocated slice will be returned.
      //
      // The dst and src must not overlap. It is valid to pass a nil dst.
      //
      // Decode handles the Snappy block format, not the Snappy stream format.
      public static Span<byte> Decode(Span<byte> dst, ReadOnlySpan<byte> src)
      {
         DecodedLen(src, out int dLen, out int s);

         if(dLen <= dst.Length)
         {
            dst = dst.Slice(0, dLen);

         }
         else
         {
            throw new NotImplementedException();

         }

         int r = DecodeInternal(dst, src.Slice(s));
         switch(r)
         {
            case 0:
               return dst;
            case DecodeErrCodeUnsupportedLiteralLength:
               throw new IOException(nameof(DecodeErrCodeUnsupportedLiteralLength));
         }

         throw new IOException(ErrCorrupt);
      }
   }
}
