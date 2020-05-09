using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace IronSnappy
{
   class SnappyWriter : Stream
   {
      private readonly Stream _parent;
      private readonly byte[] _ibuf;
      private readonly byte[] _obuf;
      private int _ibufIdx;
      private bool _wroteStreamHeader;

      public SnappyWriter(Stream parent)
      {
         _parent = parent;

         _ibuf = Snappy.BytePool.Rent(Snappy.MaxBlockSize);
         _obuf = Snappy.BytePool.Rent(Snappy.ObufLen);
      }

      public override bool CanRead => false;

      public override bool CanSeek => false;

      public override bool CanWrite => true;

      public override long Length => _parent.Length;

      public override long Position { get => _parent.Position; set => throw new NotSupportedException(); }

      public override void Flush()
      {
         if(_ibufIdx == 0)
            return;

         WriteChunk(_ibuf.AsSpan(0, _ibufIdx));
         _ibufIdx = 0;
      }

      public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
      public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
      public override void SetLength(long value) => throw new NotSupportedException();

      public override void Write(byte[] buffer, int offset, int count)
      {
         Span<byte> src = buffer.AsSpan(offset, count);

         while(src.Length > (_ibuf.Length - _ibufIdx))
         {
            int copyMax;

            if(_ibufIdx == 0)
            {
               // large write, empty buffer, can write directly from src
               WriteChunk(src);
               copyMax = src.Length;
            }
            else
            {
               //append to ibuf what we can
               copyMax = _ibuf.Length - _ibufIdx;
               src.Slice(0, copyMax).CopyTo(_ibuf.AsSpan(_ibufIdx));
               _ibufIdx += copyMax;

               Flush();
            }

            src = src.Slice(copyMax);
         }

         //copy remaining data
         int copyMaxLeft = Math.Min(_ibuf.Length - _ibufIdx, src.Length);
         src.Slice(0, copyMaxLeft).CopyTo(_ibuf.AsSpan(_ibufIdx));
         _ibufIdx += copyMaxLeft;
      }

      protected override void Dispose(bool disposing)
      {
         try
         {
            Flush();
         }
         finally
         {
            Snappy.BytePool.Return(_ibuf);
            Snappy.BytePool.Return(_obuf);
         }
      }

      private void WriteChunk(ReadOnlySpan<byte> p)
      {
         while(p.Length > 0)
         {
            int obufStart = Snappy.MagicChunk.Length;

            if(!_wroteStreamHeader)
            {
               _wroteStreamHeader = true;
               Encoding.UTF8.GetBytes(Snappy.MagicChunk).AsSpan().CopyTo(_obuf.AsSpan());
               obufStart = 0;
            }

            ReadOnlySpan<byte> uncompressed;
            if(p.Length > Snappy.MaxBlockSize)
            {
               uncompressed = p.Slice(0, Snappy.MaxBlockSize);
               p = p.Slice(Snappy.MaxBlockSize);
            }
            else
            {
               uncompressed = p;
               p = null;
            }

            uint checksum = Crc32.Compute(uncompressed);

            // Compress the buffer, discarding the result if the improvement
            // isn't at least 12.5%.
            ReadOnlySpan<byte> compressed = Encode(_obuf.AsSpan(Snappy.ObufHeaderLen), uncompressed);
            byte chunkType = (byte)Snappy.ChunkTypeCompressedData;
            int chunkLen = 4 + compressed.Length;
            int obufEnd = Snappy.ObufHeaderLen + compressed.Length;

            if(compressed.Length >= uncompressed.Length - uncompressed.Length / 8)
            {
               chunkType = Snappy.ChunkTypeUncompressedData;

               chunkLen = 4 + uncompressed.Length;

               obufEnd = Snappy.ObufHeaderLen;

            }

            // Fill in the per-chunk header that comes before the body.
            _obuf[Snappy.MagicChunk.Length + 0] = chunkType;
            _obuf[Snappy.MagicChunk.Length + 1] = (byte)(chunkLen >> 0);
            _obuf[Snappy.MagicChunk.Length + 2] = (byte)(chunkLen >> 8);
            _obuf[Snappy.MagicChunk.Length + 3] = (byte)(chunkLen >> 16);
            _obuf[Snappy.MagicChunk.Length + 4] = (byte)(checksum >> 0);
            _obuf[Snappy.MagicChunk.Length + 5] = (byte)(checksum >> 8);
            _obuf[Snappy.MagicChunk.Length + 6] = (byte)(checksum >> 16);
            _obuf[Snappy.MagicChunk.Length + 7] = (byte)(checksum >> 24);

            _parent.Write(_obuf, obufStart, obufEnd);

            if(chunkType == Snappy.ChunkTypeUncompressedData)
            {
               Spans.Write(_parent, compressed);
            }
         }
      }

      public static ReadOnlySpan<byte> Encode(Span<byte> dst, ReadOnlySpan<byte> src)
      {
         int n = GetMaxEncodedLen(src.Length);
         if(n < 0)
         {
            throw new ArgumentException("block is too large", nameof(src));
         }
         else if(dst.Length < n)
         {
            throw new NotImplementedException();
         }

         // The block starts with the varint-encoded length of the decompressed bytes.
         int d = PutUvarint(dst, (ulong)src.Length);

         while(src.Length > 0)
         {
            ReadOnlySpan<byte> p = src;
            src = null;

            if(p.Length > Snappy.MaxBlockSize)
            {
               p = p.Slice(0, Snappy.MaxBlockSize);
               src = p.Slice(Snappy.MaxBlockSize);
            }

            if(p.Length < Snappy.MinNonLiteralBlockSize)
            {
               d += EmitLiteral(dst.Slice(d), p);
            }
            else
            {
               d += EncodeBlock(dst.Slice(d), p);
            }
         }

         return dst.Slice(0, d);
      }

      public static int GetMaxEncodedLen(int srcLen)
      {
         uint n = (uint)srcLen;
	      if(n > 0xffffffff)
         {
            return -1;
	      }
         // Compressed data can be defined as:
         //    compressed := item* literal*
         //    item       := literal* copy
         //
         // The trailing literal sequence has a space blowup of at most 62/60
         // since a literal of length 60 needs one tag byte + one extra byte
         // for length information.
         //
         // Item blowup is trickier to measure. Suppose the "copy" op copies
         // 4 bytes of data. Because of a special check in the encoding code,
         // we produce a 4-byte copy only if the offset is < 65536. Therefore
         // the copy op takes 3 bytes to encode, and this type of item leads
         // to at most the 62/60 blowup for representing literals.
         //
         // Suppose the "copy" op copies 5 bytes of data. If the offset is big
         // enough, it will take 5 bytes to encode the copy op. Therefore the
         // worst case here is a one-byte literal followed by a five-byte copy.
         // That is, 6 bytes of input turn into 7 bytes of "compressed" data.
         //
         // This last factor dominates the blowup, so the final estimate is:
         n = 32 + n + n / 6;
	      if(n > 0xffffffff)
         {
            return -1;
	      }
         return (int)n;
      }

      // PutUvarint encodes a uint64 into buf and returns the number of bytes written.
      // If the buffer is too small, PutUvarint will panic.
      static int PutUvarint(Span<byte> buf, ulong x)
      {
         int i = 0;
         while(x >= 0x80)
         {
            buf[i] = (byte)((byte)x | 0x80);
            x >>= 7;
            i++;
         }
         buf[i] = (byte)x;
         return i + 1;
      }

      static uint Load32(ReadOnlySpan<byte> b, int i)
      {
         return BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(i, 4));
      }

      static ulong Load64(ReadOnlySpan<byte> b, int i)
      {
         return BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(i, 8));
      }

      static uint Hash(uint u, int shift)
      {
         return (u * 0x1e35a7bd) >> shift;
      }

      // encodeBlock encodes a non-empty src to a guaranteed-large-enough dst. It
      // assumes that the varint-encoded length of the decompressed bytes has already
      // been written.
      //
      // It also assumes that:
      //	len(dst) >= MaxEncodedLen(len(src)) &&
      // 	minNonLiteralBlockSize <= len(src) && len(src) <= maxBlockSize
      static int EncodeBlock(Span<byte> dst, ReadOnlySpan<byte> src)
      {
         int d = 0;

         // Initialize the hash table. Its size ranges from 1<<8 to 1<<14 inclusive.
         // The table element type is uint16, as s < sLimit and sLimit < len(src)
         // and len(src) <= maxBlockSize and maxBlockSize == 65536.
         const int maxTableSize = 1 << 14;

         // tableMask is redundant, but helps the compiler eliminate bounds
         // checks.
         const int tableMask = maxTableSize - 1;

         int shift = 32 - 8;

         for(int tableSize = 1 << 8; tableSize < maxTableSize && tableSize < src.Length; tableSize *= 2)
         {
            shift--;
         }

         // In Go, all array elements are zero-initialized, so there is no advantage
         // to a smaller tableSize per se. However, it matches the C++ algorithm,
         // and in the asm versions of this code, we can get away with zeroing only
         // the first tableSize elements.
         ushort[] table = new ushort[maxTableSize];

         // sLimit is when to stop looking for offset/length copies. The inputMargin
         // lets us use a fast path for emitLiteral in the main loop, while we are
         // looking for copies.
         int sLimit = src.Length - Snappy.InputMargin;

         // nextEmit is where in src the next emitLiteral should start from.
         int nextEmit = 0;

         // The encoded form must start with a literal, as there are no previous
         // bytes to copy, so we start looking for hash matches at s == 1.
         int s = 1;
         uint nextHash = Hash(Load32(src, s), shift);

         while(true)
         {
            // Copied from the C++ snappy implementation:
            //
            // Heuristic match skipping: If 32 bytes are scanned with no matches
            // found, start looking only at every other byte. If 32 more bytes are
            // scanned (or skipped), look at every third byte, etc.. When a match
            // is found, immediately go back to looking at every byte. This is a
            // small loss (~5% performance, ~0.1% density) for compressible data
            // due to more bookkeeping, but for non-compressible data (such as
            // JPEG) it's a huge win since the compressor quickly "realizes" the
            // data is incompressible and doesn't bother looking for matches
            // everywhere.
            //
            // The "skip" variable keeps track of how many bytes there are since
            // the last match; dividing it by 32 (ie. right-shifting by five) gives
            // the number of bytes to move ahead for each iteration.
            int skip = 32;

            int nextS = s;
            int candidate = 0;

            while(true)
            {
               s = nextS;
               int bytesBetweenHashLookups = skip >> 5;
               nextS = s + bytesBetweenHashLookups;
               skip += bytesBetweenHashLookups;
               if(nextS > sLimit)
               {
                  goto emitRemainder;
               }
               candidate = (int)(table[nextHash & tableMask]);
               table[nextHash & tableMask] = (ushort)s;

               nextHash = Hash(Load32(src, nextS), shift);
               if(Load32(src, s) == Load32(src, candidate))
               {
                  break;

               }
            }

            // A 4-byte match has been found. We'll later see if more than 4 bytes
            // match. But, prior to the match, src[nextEmit:s] are unmatched. Emit
            // them as literal bytes.
            d += EmitLiteral(dst.Slice(d), src.Slice(nextEmit, s - nextEmit));

            // Call emitCopy, and then see if another emitCopy could be our next
            // move. Repeat until we find no match for the input immediately after
            // what was consumed by the last emitCopy call.
            //
            // If we exit this loop normally then we need to call emitLiteral next,
            // though we don't yet know how big the literal will be. We handle that
            // by proceeding to the next iteration of the main loop. We also can
            // exit this loop via goto if we get close to exhausting the input.
            while(true)
            {
               // Invariant: we have a 4-byte match at s, and no need to emit any
               // literal bytes prior to s.
               int base1 = s;

               // Extend the 4-byte match as long as possible.
               //
               // This is an inlined version of:
               //	s = extendMatch(src, candidate+4, s+4)
               s += 4;
               for(int i = candidate + 4; s < src.Length && src[i] == src[s];)
               {
                  i = i + 1;
                  s = s + 1;
               }

               d += EmitCopy(dst.Slice(d), base1 - candidate, s - base1);

               nextEmit = s;
               if(s >= sLimit)
               {
                  goto emitRemainder;
               }

               // We could immediately start working at s now, but to improve
               // compression we first update the hash table at s-1 and at s. If
               // another emitCopy is not our next move, also calculate nextHash
               // at s+1. At least on GOARCH=amd64, these three hash calculations
               // are faster as one load64 call (with some shifts) instead of
               // three load32 calls.
               ulong x = Load64(src, s - 1);

               uint prevHash = Hash((uint)(x >> 0), shift);
               table[prevHash & tableMask] = (ushort)(s - 1);

               uint currHash = Hash((uint)(x >> 8), shift);
               candidate = (int)(table[currHash & tableMask]);
               table[currHash & tableMask] = (ushort)(s);
               if((uint)(x >> 8) != Load32(src, candidate))
               {
                  nextHash = Hash((uint)(x >> 16), shift);

                  s++;

                  break;

               }
            }
         }

         emitRemainder:
         if(nextEmit < src.Length)
         {
            d += EmitLiteral(dst.Slice(d), src.Slice(nextEmit));
         }
         return d;

      }

      // emitCopy writes a copy chunk and returns the number of bytes written.
      //
      // It assumes that:
      //	dst is long enough to hold the encoded bytes
      //	1 <= offset && offset <= 65535
      //	4 <= length && length <= 65535
      static int EmitCopy(Span<byte> dst, int offset, int length)
      {
         int i = 0;

         // The maximum length for a single tagCopy1 or tagCopy2 op is 64 bytes. The
         // threshold for this loop is a little higher (at 68 = 64 + 4), and the
         // length emitted down below is is a little lower (at 60 = 64 - 4), because
         // it's shorter to encode a length 67 copy as a length 60 tagCopy2 followed
         // by a length 7 tagCopy1 (which encodes as 3+2 bytes) than to encode it as
         // a length 64 tagCopy2 followed by a length 3 tagCopy2 (which encodes as
         // 3+3 bytes). The magic 4 in the 64±4 is because the minimum length for a
         // tagCopy1 op is 4 bytes, which is why a length 3 copy has to be an
         // encodes-as-3-bytes tagCopy2 instead of an encodes-as-2-bytes tagCopy1.
         while(length >= 68)
         {
            // Emit a length 64 copy, encoded as 3 bytes.
            dst[i + 0] = 63 << 2 | Snappy.TagCopy2;
            dst[i + 1] = (byte)offset;

            dst[i + 2] = (byte)(offset >> 8);

            i += 3;
            length -= 64;
         }

         if(length > 64)
         {
            // Emit a length 60 copy, encoded as 3 bytes.
            dst[i + 0] = 59 << 2 | Snappy.TagCopy2;
            dst[i + 1] = (byte)offset;

            dst[i + 2] = (byte)(offset >> 8);

            i += 3;
            length -= 60;
         }

         if(length >= 12 || offset >= 2048)
         {
            // Emit the remaining copy, encoded as 3 bytes.
            dst[i + 0] = (byte)((ushort)(length - 1) << 2 | Snappy.TagCopy2);
            dst[i + 1] = (byte)offset;

            dst[i + 2] = (byte)(offset >> 8);
            return i + 3;
         }

         // Emit the remaining copy, encoded as 2 bytes.
         dst[i + 0] = (byte)((uint)(offset >> 8) << 5 | (uint)(length - 4) << 2 | Snappy.TagCopy1);
         dst[i + 1] = (byte)offset;
         return i + 2;
      }

      // emitLiteral writes a literal chunk and returns the number of bytes written.
      //
      // It assumes that:
      //	dst is long enough to hold the encoded bytes
      //	1 <= len(lit) && len(lit) <= 65536
      static int EmitLiteral(Span<byte> dst, ReadOnlySpan<byte> lit)
      {
         int i = 0;
         int n = lit.Length - 1;

         if(n < 60)
         {
            dst[0] = (byte)((n << 2) | Snappy.TagLiteral);
            i = 1;
         }
         else if(n < 1 << 8)
         {

            dst[0] = 60 << 2 | Snappy.TagLiteral;

            dst[1] = (byte)n;
            i = 2;
         }
         else
         {
            dst[0] = 61 << 2 | Snappy.TagLiteral;
            dst[1] = (byte)n;

            dst[2] = (byte)(n >> 8);

            i = 3;
         }

         lit.CopyTo(dst.Slice(i));
         return i + lit.Length;
      }
   }
}
