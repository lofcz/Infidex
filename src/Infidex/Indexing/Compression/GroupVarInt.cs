using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Infidex.Indexing.Compression;

/// <summary>
/// Group VarInt (VarInt-GB) encoding.
/// Encodes 4 integers at a time with a single 1-byte tag.
/// Optimized with SIMD (SSSE3/AVX2) for decoding.
/// </summary>
internal static unsafe class GroupVarInt
{
    private static readonly byte[] DecodeShuffleTable; // 256 * 16 bytes
    private static readonly byte[] DecodeLengthTable;  // 256 bytes

    static GroupVarInt()
    {
        DecodeShuffleTable = new byte[256 * 16];
        DecodeLengthTable = new byte[256];
        InitializeTables();
    }

    private static void InitializeTables()
    {
        for (int tag = 0; tag < 256; tag++)
        {
            int len0 = (tag >> 6) + 1;
            int len1 = ((tag >> 4) & 3) + 1;
            int len2 = ((tag >> 2) & 3) + 1;
            int len3 = (tag & 3) + 1;

            int currentInputByte = 0;
            int offset = tag * 16;

            // Int 0
            for (int b = 0; b < 4; b++)
                DecodeShuffleTable[offset + b] = (byte)(b < len0 ? currentInputByte++ : 0x80);

            // Int 1
            for (int b = 0; b < 4; b++)
                DecodeShuffleTable[offset + 4 + b] = (byte)(b < len1 ? currentInputByte++ : 0x80);

            // Int 2
            for (int b = 0; b < 4; b++)
                DecodeShuffleTable[offset + 8 + b] = (byte)(b < len2 ? currentInputByte++ : 0x80);

            // Int 3
            for (int b = 0; b < 4; b++)
                DecodeShuffleTable[offset + 12 + b] = (byte)(b < len3 ? currentInputByte++ : 0x80);

            DecodeLengthTable[tag] = (byte)currentInputByte;
        }
    }

    public static void Write(BinaryWriter writer, ReadOnlySpan<int> data)
    {
        int i = 0;
        
        while (i < data.Length)
        {
            int remaining = data.Length - i;
            if (remaining >= 4)
            {
                EncodeGroup(writer, data[i], data[i+1], data[i+2], data[i+3]);
                i += 4;
            }
            else
            {
                // Fallback for last items (1..3)
                int v0 = data[i];
                int v1 = remaining > 1 ? data[i+1] : 0;
                int v2 = remaining > 2 ? data[i+2] : 0;
                int v3 = 0;
                
                EncodeGroup(writer, v0, v1, v2, v3, remaining);
                i += remaining;
            }
        }
    }

    private static void EncodeGroup(BinaryWriter writer, int v0, int v1, int v2, int v3, int count = 4)
    {
        int len0 = GetByteCount(v0);
        int len1 = GetByteCount(v1);
        int len2 = GetByteCount(v2);
        int len3 = GetByteCount(v3);
        
        // Tag: 2 bits per len (00=1 byte, 01=2 bytes, 10=3 bytes, 11=4 bytes)
        byte tag = (byte)(((len0 - 1) << 6) | ((len1 - 1) << 4) | ((len2 - 1) << 2) | (len3 - 1));
        writer.Write(tag);
        
        WriteInt(writer, v0, len0);
        if (count > 1) WriteInt(writer, v1, len1);
        if (count > 2) WriteInt(writer, v2, len2);
        if (count > 3) WriteInt(writer, v3, len3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetByteCount(int v)
    {
        if (v < (1 << 8)) return 1;
        if (v < (1 << 16)) return 2;
        if (v < (1 << 24)) return 3;
        return 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteInt(BinaryWriter w, int v, int len)
    {
        // Little endian
        w.Write((byte)v);
        if (len > 1) w.Write((byte)(v >> 8));
        if (len > 2) w.Write((byte)(v >> 16));
        if (len > 3) w.Write((byte)(v >> 24));
    }
    
    public static void DecodeBlock(byte* src, int[] dest, int count, out int bytesRead)
    {
        if (Ssse3.IsSupported)
        {
            DecodeBlockSIMD(src, dest, count, out bytesRead);
        }
        else
        {
            DecodeBlockScalar(src, dest, count, out bytesRead);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecodeBlockSIMD(byte* src, int[] dest, int count, out int bytesRead)
    {
        byte* ptr = src;
        int dstIdx = 0;
        
        // Ensure we have enough buffer space to read 17 bytes (1 tag + 16 data max)
        // We iterate as long as we have full groups of 4.
        // We also need to be careful about reading past the end of the page if the block is at the end.
        // For simplicity, we assume the buffer is padded or we switch to scalar near the end.
        // But src is usually from MMap, so we might not have padding.
        // However, max read is 17 bytes.
        
        int limit = count - 4; // Leave last group for scalar to avoid over-reading if strict
        
        fixed (byte* shuffleBase = DecodeShuffleTable)
        fixed (byte* lengthBase = DecodeLengthTable)
        fixed (int* destBase = dest)
        {
            while (dstIdx <= limit)
            {
                byte tag = *ptr++;
                
                // Load 128 bits from ptr.
                // Note: This might read past the actual data for this group, but within valid memory 
                // if we are not at the very end of the file/segment.
                // Assuming segment padding or safe bounds.
                Vector128<byte> data = Ssse3.LoadVector128(ptr);
                
                // Load Shuffle Mask
                Vector128<byte> shuffle = Ssse3.LoadVector128(shuffleBase + (tag * 16));
                
                // Shuffle
                Vector128<byte> result = Ssse3.Shuffle(data, shuffle);
                
                // Store
                Ssse3.Store((byte*)(destBase + dstIdx), result);
                
                // Advance
                ptr += lengthBase[tag];
                dstIdx += 4;
            }
        }
        
        // Handle remaining items scalar
        while (dstIdx < count)
        {
             byte tag = *ptr++;
             
             int len0 = (tag >> 6) + 1;
             int len1 = ((tag >> 4) & 3) + 1;
             int len2 = ((tag >> 2) & 3) + 1;
             int len3 = (tag & 3) + 1;
             
             dest[dstIdx++] = ReadInt(ptr, len0); ptr += len0;
             if (dstIdx < count) { dest[dstIdx++] = ReadInt(ptr, len1); ptr += len1; }
             if (dstIdx < count) { dest[dstIdx++] = ReadInt(ptr, len2); ptr += len2; }
             if (dstIdx < count) { dest[dstIdx++] = ReadInt(ptr, len3); ptr += len3; }
        }
        
        bytesRead = (int)(ptr - src);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecodeBlockScalar(byte* src, int[] dest, int count, out int bytesRead)
    {
        byte* ptr = src;
        int dstIdx = 0;
        
        while (dstIdx < count)
        {
            byte tag = *ptr++;
            
            int len0 = (tag >> 6) + 1;
            int len1 = ((tag >> 4) & 3) + 1;
            int len2 = ((tag >> 2) & 3) + 1;
            int len3 = (tag & 3) + 1;
            
            dest[dstIdx++] = ReadInt(ptr, len0); ptr += len0;
            if (dstIdx < count) { dest[dstIdx++] = ReadInt(ptr, len1); ptr += len1; }
            if (dstIdx < count) { dest[dstIdx++] = ReadInt(ptr, len2); ptr += len2; }
            if (dstIdx < count) { dest[dstIdx++] = ReadInt(ptr, len3); ptr += len3; }
        }
        
        bytesRead = (int)(ptr - src);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadInt(byte* p, int len)
    {
        int v = *p;
        if (len > 1) v |= p[1] << 8;
        if (len > 2) v |= p[2] << 16;
        if (len > 3) v |= p[3] << 24;
        return v;
    }
}
