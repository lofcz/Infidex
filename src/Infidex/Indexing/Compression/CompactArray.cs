using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Infidex.Indexing.Compression;

/// <summary>
/// Stores a list of n-bit integers packed tightly into a ulong array.
/// Ported from the reference CompactArray.zig.
/// </summary>
internal sealed class CompactArray
{
    private readonly ulong mask;

    public int Width { get; }

    public int Count { get; }

    public ulong[] Data { get; }

    public CompactArray(ulong[] data, int width, int count)
    {
        Data = data;
        Width = width;
        Count = count;
        mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
    }

    /// <summary>
    /// Gets the value at the specified index.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Get(int index)
    {
        long pos = (long)index * Width;
        int block = (int)(pos >> 6);
        int shift = (int)(pos & 63);

        if (shift + Width <= 64)
        {
            return (Data[block] >> shift) & mask;
        }

        int resShift = 64 - shift;
        return ((Data[block] >> shift) | (Data[block + 1] << resShift)) & mask;
    }

    /// <summary>
    /// Creates a CompactArray from a list of values.
    /// </summary>
    public static CompactArray Create(ReadOnlySpan<long> values)
    {
        if (values.Length == 0)
            return new CompactArray([], 1, 0);
        
        ulong max = 0;
        foreach (long val in values)
        {
            if ((ulong)val > max) max = (ulong)val;
        }

        int width = max == 0 ? 1 : 64 - BitOperations.LeadingZeroCount(max);
        
        long totalBits = (long)values.Length * width;
        int ulongCount = (int)((totalBits + 63) / 64);
        ulong[] data = new ulong[ulongCount];
        
        for (int i = 0; i < values.Length; i++)
        {
            SetFromZero(data, width, i, (ulong)values[i]);
        }

        return new CompactArray(data, width, values.Length);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetFromZero(ulong[] data, int width, int index, ulong value)
    {
        long pos = (long)index * width;
        int block = (int)(pos >> 6);
        int shift = (int)(pos & 63);

        data[block] |= value << shift;

        if (shift + width > 64)
        {
            int resShift = 64 - shift;
            data[block + 1] |= value >> resShift;
        }
    }
    
    public void Write(BinaryWriter writer)
    {
        writer.Write(Width);
        writer.Write(Count);
        writer.Write(Data.Length);
        
        if (BitConverter.IsLittleEndian)
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(Data.AsSpan());
            writer.Write(bytes);
        }
        else
        {
            for (int i = 0; i < Data.Length; i++)
                writer.Write(Data[i]);
        }
    }

    public static CompactArray Read(BinaryReader reader)
    {
        int width = reader.ReadInt32();
        int count = reader.ReadInt32();
        int dataLen = reader.ReadInt32();
        ulong[] data = new ulong[dataLen];

        if (BitConverter.IsLittleEndian)
        {
            Span<byte> bytes = MemoryMarshal.AsBytes(data.AsSpan());
            int bytesRead = reader.Read(bytes);
            if (bytesRead != bytes.Length)
                throw new EndOfStreamException();
        }
        else
        {
            for (int i = 0; i < dataLen; i++)
                data[i] = reader.ReadUInt64();
        }

        return new CompactArray(data, width, count);
    }
}
