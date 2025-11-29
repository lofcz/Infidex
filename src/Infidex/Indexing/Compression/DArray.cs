using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace Infidex.Indexing.Compression;

/// <summary>
/// Implements the "darray" data structure which provides constant-time
/// select(i) operation for dense bit sets.
/// </summary>
internal sealed class DArray
{
    private const int BlockSize = 1024;
    private const int SubBlockSize = 32;
    private const int MaxInBlockDistance = 1 << 16;

    private readonly ulong[] _blockInventory;
    private readonly ushort[] _subBlockInventory;
    private readonly long[] _overflowPositions;
    private readonly bool _select1;

    public DArray(
        ulong[] blockInventory,
        ushort[] subBlockInventory,
        long[] overflowPositions,
        bool select1)
    {
        _blockInventory = blockInventory;
        _subBlockInventory = subBlockInventory;
        _overflowPositions = overflowPositions;
        _select1 = select1;
    }

    /// <summary>
    /// Builds a DArray from a BitSet.
    /// </summary>
    public static DArray Build(BitSet bitSet, bool select1 = true)
    {
        List<long> curBlockPositions = [];
        List<ulong> blockInventory = [];
        List<ushort> subBlockInventory = [];
        List<long> overflowPositions = [];
        
        int len = bitSet.Length;
        ulong[] words = bitSet.Words;

        for (int i = 0; i < words.Length; i++)
        {
            ulong w = words[i];
            if (!select1) w = ~w;
            
            if (i == words.Length - 1)
            {
                int remaining = len % 64;
                if (remaining > 0)
                {
                    ulong mask = (1UL << remaining) - 1;
                    w &= mask;
                }
            }

            while (w != 0)
            {
                int tz = BitOperations.TrailingZeroCount(w);
                long globalPos = (long)i * 64 + tz;
                
                curBlockPositions.Add(globalPos);
                if (curBlockPositions.Count == BlockSize)
                {
                    FlushCurBlock(curBlockPositions, blockInventory, subBlockInventory, overflowPositions);
                }

                w &= w - 1;
            }
        }

        if (curBlockPositions.Count > 0)
        {
            FlushCurBlock(curBlockPositions, blockInventory, subBlockInventory, overflowPositions);
        }

        return new DArray(
            blockInventory.ToArray(),
            subBlockInventory.ToArray(),
            overflowPositions.ToArray(),
            select1);
    }

    private static void FlushCurBlock(
        List<long> curBlockPositions,
        List<ulong> blockInventory,
        List<ushort> subBlockInventory,
        List<long> overflowPositions)
    {
        long fst = curBlockPositions[0];
        long lst = curBlockPositions[^1];

        if (lst - fst < MaxInBlockDistance)
        {
            blockInventory.Add((ulong)fst & 0x7FFFFFFFFFFFFFFFUL);

            for (int i = 0; i < curBlockPositions.Count; i += SubBlockSize)
            {
                subBlockInventory.Add((ushort)(curBlockPositions[i] - fst));
            }
        }
        else
        {
            long overflowPos = overflowPositions.Count;
            blockInventory.Add((ulong)overflowPos | 0x8000000000000000UL);

            foreach (long pos in curBlockPositions)
            {
                overflowPositions.Add(pos);
            }
            
            for (int i = 0; i < curBlockPositions.Count; i += SubBlockSize)
            {
                subBlockInventory.Add(0);
            }
        }

        curBlockPositions.Clear();
    }

    /// <summary>
    /// Returns the position of the i-th set bit (if select1) or unset bit (if select0).
    /// Index i is 0-based.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Select(BitSet bitSet, long idx)
    {
        int block = (int)(idx / BlockSize);
        if (block >= _blockInventory.Length) return -1;

        ulong blockEntry = _blockInventory[block];
        bool isOverflow = (blockEntry & 0x8000000000000000UL) != 0;
        long blockPos = (long)(blockEntry & 0x7FFFFFFFFFFFFFFFUL);

        if (isOverflow)
        {
            long offset = idx % BlockSize;
            return _overflowPositions[blockPos + offset];
        }

        int subBlock = (int)(idx / SubBlockSize);
        long startPos = blockPos + _subBlockInventory[subBlock];
        
        long reminder = idx % SubBlockSize;
        if (reminder == 0) return startPos;

        int wordIdx = (int)(startPos >> 6);
        int wordShift = (int)(startPos & 63);

        ulong word = bitSet.Words[wordIdx];
        if (!_select1) word = ~word;

        word &= ~0UL << wordShift;

        while (true)
        {
            int pop = BitOperations.PopCount(word);
            if (reminder < pop) break;
            
            reminder -= pop;
            wordIdx++;
            word = bitSet.Words[wordIdx];
            if (!_select1) word = ~word;
        }
        
        if (Bmi2.X64.IsSupported)
        {
            ulong result = Bmi2.X64.ParallelBitDeposit(1UL << (int)reminder, word);
            return ((long)wordIdx << 6) + BitOperations.TrailingZeroCount(result);
        }

        while (true)
        {
            int tz = BitOperations.TrailingZeroCount(word);
            if (reminder == 0)
            {
                return ((long)wordIdx << 6) + tz;
            }
            reminder--;
            word &= word - 1;
        }
    }

    public void Write(BinaryWriter writer)
    {
        writer.Write(_blockInventory.Length);
        for (int i = 0; i < _blockInventory.Length; i++)
            writer.Write(_blockInventory[i]);
        
        writer.Write(_subBlockInventory.Length);
        for (int i = 0; i < _subBlockInventory.Length; i++)
            writer.Write(_subBlockInventory[i]);
        
        writer.Write(_overflowPositions.Length);
        for (int i = 0; i < _overflowPositions.Length; i++)
            writer.Write(_overflowPositions[i]);
    }

    public static DArray Read(BinaryReader reader, bool select1 = true)
    {
        int blockInvLen = reader.ReadInt32();
        ulong[] blockInventory = new ulong[blockInvLen];
        for (int i = 0; i < blockInvLen; i++)
            blockInventory[i] = reader.ReadUInt64();
        
        int subBlockInvLen = reader.ReadInt32();
        ushort[] subBlockInventory = new ushort[subBlockInvLen];
        for (int i = 0; i < subBlockInvLen; i++)
            subBlockInventory[i] = reader.ReadUInt16();
        
        int overflowLen = reader.ReadInt32();
        long[] overflowPositions = new long[overflowLen];
        for (int i = 0; i < overflowLen; i++)
            overflowPositions[i] = reader.ReadInt64();

        return new DArray(blockInventory, subBlockInventory, overflowPositions, select1);
    }
}
