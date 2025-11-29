using System.Numerics;
using System.Runtime.CompilerServices;

namespace Infidex.Indexing.Compression;

/// <summary>
/// A dense bit set backed by an ulong array.
/// </summary>
internal sealed class BitSet
{
    public readonly ulong[] Words;
    public readonly int Length;

    public BitSet(int length)
    {
        Length = length;
        int wordCount = (length + 63) / 64;
        Words = new ulong[wordCount];
    }

    public BitSet(ulong[] words, int length)
    {
        Words = words;
        Length = length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int index)
    {
        if ((uint)index >= (uint)Length) throw new IndexOutOfRangeException();
        Words[index >> 6] |= 1UL << (index & 63);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Get(int index)
    {
        if ((uint)index >= (uint)Length) throw new IndexOutOfRangeException();
        return (Words[index >> 6] & (1UL << (index & 63))) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PopCount()
    {
        int count = 0;
        foreach (ulong w in Words)
            count += BitOperations.PopCount(w);
        return count;
    }
}