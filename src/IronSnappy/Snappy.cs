using System;
using System.Buffers;
using System.IO;

namespace IronSnappy
{
   public static class Snappy
   {
      internal const string MagicBody = "sNaPpY";
      internal static byte[] MagicChunk = new byte[] { 0xff, 0x06, 0x00, 0x00, (byte)'s', (byte)'N', (byte)'a', (byte)'P', (byte)'p', (byte)'Y' };
      internal const int MaxBlockSize = 65536;
      internal const int ChecksumSize = 4;
      private const int ChunkHeaderSize = 4;
      internal const int MaxEncodedLenOfMaxBlockSize = 76490;
      internal const int InputMargin = 16 - 1;
      internal const int MinNonLiteralBlockSize = 1 + 1 + InputMargin;

      internal const int TagLiteral = 0x00;
      internal const int TagCopy1 = 0x01;
      internal const int TagCopy2 = 0x02;
      internal const int TagCopy4 = 0x03;

      internal const int ChunkTypeCompressedData = 0x00;
      internal const int ChunkTypeUncompressedData = 0x01;
      private const int ChunkTypePadding = 0xfe;
      internal const byte ChunkTypeStreamIdentifier = 0xff;

      internal static readonly int ObufHeaderLen = MagicChunk.Length + ChecksumSize + ChunkHeaderSize;
      internal static readonly int ObufLen = ObufHeaderLen + MaxEncodedLenOfMaxBlockSize;

      internal static readonly ArrayPool<byte> BytePool = ArrayPool<byte>.Shared;

      public static Stream OpenWriter(Stream destination)
      {
         return new SnappyWriter(destination);
      }

      public static Stream OpenReader(Stream source)
      {
         return new SnappyReader(source);
      }

      public static byte[] Encode(ReadOnlySpan<byte> src)
      {
         int maxLen = SnappyWriter.GetMaxEncodedLen(src.Length);

         using(var dst = new RentedBuffer(maxLen))
         {
            ReadOnlySpan<byte> compressed = SnappyWriter.Encode(dst.Span, src);

            return compressed.ToArray();
         }
      }

      public static byte[] Decode(ReadOnlySpan<byte> src)
      {
         int dLen = SnappyReader.DecodedLen(src);

         using(var dst = new RentedBuffer(dLen))
         {
            Span<byte> uncompressed = SnappyReader.Decode(dst.Span, src);

            return uncompressed.ToArray();
         }
      }
   }
}
