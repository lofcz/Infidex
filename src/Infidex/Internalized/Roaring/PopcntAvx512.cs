using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Infidex.Internalized.Roaring;

// Based on Harley-Seal
internal static class PopcntAvx512
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Popcnt(ReadOnlySpan<ulong> longs)
    {
        ref Vector512<uint> start = ref Unsafe.As<ulong, Vector512<uint>>(ref MemoryMarshal.GetReference(longs));
        ulong total = Popcnt(ref start, longs.Length / 8);

        // Handle remaining longs
        for (int i = longs.Length - longs.Length % 4; i < longs.Length; i++)
        {
            total += (ulong)BitOperations.PopCount(longs[i]);
        }

        return total;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<ushort> PopcntVec(ref Vector512<uint> v)
    {
        Vector512<byte> m1 = Vector512.Create((byte)0x55);
        Vector512<byte> m2 = Vector512.Create((byte)0x33);
        Vector512<byte> m4 = Vector512.Create((byte)0x0F);

        Vector512<byte> t1 = Avx512BW.Subtract(v.AsByte(), Avx512F.And(Avx512BW.ShiftRightLogical(v.AsUInt16(), 1).AsByte(), m1));
        Vector512<byte> t2 = Avx512BW.Add(Avx512F.And(t1, m2), Avx512F.And(Avx512BW.ShiftRightLogical(t1.AsUInt16(), 2).AsByte(), m2));
        Vector512<byte> t3 = Avx512F.And(Avx512BW.Add(t2, Avx512BW.ShiftRightLogical(t2.AsUInt16(), 4).AsByte()), m4);
        return Avx512BW.SumAbsoluteDifferences(t3, Vector512<byte>.Zero);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CSA(out Vector512<uint> h, out Vector512<uint> l, ref Vector512<uint> a, ref Vector512<uint> b, ref Vector512<uint> c)
    {
        l = Avx512F.TernaryLogic(c, b, a, 0x96);
        h = Avx512F.TernaryLogic(c, b, a, 0xe8);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Popcnt(ref Vector512<uint> start, int size)
    {
        Vector512<ulong> total = Vector512<ulong>.Zero;
        Vector512<uint> ones = Vector512<uint>.Zero;
        Vector512<uint> twos = Vector512<uint>.Zero;

        int limit = size - size % 4;

        ref Vector512<uint> end = ref Unsafe.Add(ref start, limit);

        while (Unsafe.IsAddressLessThan(ref start, ref end))
        {
            CSA(out Vector512<uint> twosA, out ones, ref ones, ref start, ref Unsafe.Add(ref start, 1));
            CSA(out Vector512<uint> twosB, out ones, ref ones, ref Unsafe.Add(ref start, 2), ref Unsafe.Add(ref start, 3));
            CSA(out Vector512<uint> fours, out twos, ref twos, ref twosA, ref twosB);
            
            total = Avx512F.Add(total, PopcntVec(ref fours).AsUInt64());
            
            start = ref Unsafe.Add(ref start, 4);
        }

        total = Avx512F.ShiftLeftLogical(total, 4);
        total = Avx512F.Add(total, Avx512F.ShiftLeftLogical(PopcntVec(ref twos).AsUInt64(), 1));
        total = Avx512F.Add(total, PopcntVec(ref ones).AsUInt64());

        ref Vector512<uint> end2 = ref Unsafe.Add(ref start, size % 4);

        // Handle remaining vectors
        while (Unsafe.IsAddressLessThan(ref start, ref end2))
        {
            total = Avx512F.Add(total, PopcntVec(ref start).AsUInt64());
            start = ref Unsafe.Add(ref start, 1);
        }

        Vector256<ulong> sum256 = Avx2.Add(total.GetLower(), total.GetUpper());
        Vector128<ulong> sum128 = Sse2.Add(sum256.GetLower(), sum256.GetUpper());
        return sum128.GetElement(0) + sum128.GetElement(1);
    }
}
