using System.Numerics;

namespace Infidex.Indexing.Compression;

/// <summary>
/// Elias-Fano encoding for monotonic integer sequences.
/// </summary>
internal sealed class EliasFano
{
    private readonly BitSet _highBits;
    private readonly DArray _highBitsSelect;
    private readonly CompactArray _lowBits;
    private readonly int _l;

    public EliasFano(BitSet highBits, DArray highBitsSelect, CompactArray lowBits, int count, int l)
    {
        _highBits = highBits;
        _highBitsSelect = highBitsSelect;
        _lowBits = lowBits;
        Count = count;
        _l = l;
    }

    public int Count { get; }

    /// <summary>
    /// Encodes a strictly increasing sequence of integers.
    /// </summary>
    public static EliasFano Encode(ReadOnlySpan<long> data)
    {
        if (data.Length == 0)
        {
            return new EliasFano(new BitSet(0), null!, new CompactArray([], 0, 0), 0, 0);
        }

        long u = data[^1];
        int n = data.Length;
        
        int l = 0;
        if (u > n)
        {
            l = BitOperations.Log2((ulong)(u / n)) + 1;
        }

        ulong lMask = (1UL << l) - 1;
        ulong maxH = (ulong)u >> l;
        
        int highBitsLen = (int)(maxH + (ulong)n);
        BitSet highBits = new BitSet(highBitsLen);
        
        ulong[] lowBitsData = new ulong[(int)(((long)n * l + 63) / 64)];
        
        for (int i = 0; i < n; i++)
        {
            ulong val = (ulong)data[i];

            if (l > 0)
            {
                ulong low = val & lMask;
                CompactArray.SetFromZero(lowBitsData, l, i, low);
            }
            
            long highPos = (long)(val >> l) + i;
            highBits.Set((int)highPos);
        }

        CompactArray lowBits = new CompactArray(lowBitsData, l, n);
        DArray highBitsSelect = DArray.Build(highBits, select1: true);

        return new EliasFano(highBits, highBitsSelect, lowBits, n, l);
    }

    /// <summary>
    /// Gets the value at the specified index.
    /// </summary>
    public long Get(int index)
    {
        if ((uint)index >= (uint)Count) throw new IndexOutOfRangeException();

        long pos = _highBitsSelect.Select(_highBits, index);
        long high = pos - index;
        
        if (_l == 0) return high;

        ulong low = _lowBits.Get(index);
        return (high << _l) | (long)low;
    }

    public void Write(BinaryWriter writer)
    {
        writer.Write(Count);
        writer.Write(_l);

        writer.Write(_highBits.Length);
        writer.Write(_highBits.Words.Length);
        for(int i=0; i<_highBits.Words.Length; i++) 
            writer.Write(_highBits.Words[i]);
        
        _highBitsSelect.Write(writer);
        _lowBits.Write(writer);
    }
    
    public static EliasFano Read(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        int l = reader.ReadInt32();
        
        int hbLen = reader.ReadInt32();
        int hbWordsLen = reader.ReadInt32();
        ulong[] hbWords = new ulong[hbWordsLen];
        for(int i=0; i<hbWordsLen; i++) hbWords[i] = reader.ReadUInt64();
        BitSet highBits = new BitSet(hbWords, hbLen);
        DArray highBitsSelect = DArray.Read(reader, select1: true);
        CompactArray lowBits = CompactArray.Read(reader);
        
        return new EliasFano(highBits, highBitsSelect, lowBits, count, l);
    }
}
