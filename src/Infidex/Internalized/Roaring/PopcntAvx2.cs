using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Infidex.Internalized.Roaring;

// Based on Harley-Seal
internal static class PopcntAvx2
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Popcnt(ReadOnlySpan<ulong> longs)
    {
        ref Vector256<long> start = ref Unsafe.As<ulong, Vector256<long>>(ref MemoryMarshal.GetReference(longs));
        ulong total = Popcnt(ref start, longs.Length / 4);

        // Handle remaining longs
        for (int i = longs.Length - longs.Length % 4; i < longs.Length; i++)
        {
            total += (ulong)BitOperations.PopCount(longs[i]);
        }

        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<long> PopcntVec(ref Vector256<long> v)
    {
        Vector256<byte> m1 = Vector256.Create((byte)0x55);
        Vector256<byte> m2 = Vector256.Create((byte)0x33);
        Vector256<byte> m4 = Vector256.Create((byte)0x0F);
        
        Vector256<byte> t1 = Avx2.Subtract(v.AsByte(), Avx2.And(Avx2.ShiftRightLogical(v.AsUInt16(), 1).AsByte(), m1));
        Vector256<byte> t2 = Avx2.Add(Avx2.And(t1, m2), Avx2.And(Avx2.ShiftRightLogical(t1.AsUInt16(), 2).AsByte(), m2));
        Vector256<byte> t3 = Avx2.And(Avx2.Add(t2, Avx2.ShiftRightLogical(t2.AsUInt16(), 4).AsByte()), m4);
        return Avx2.SumAbsoluteDifferences(t3, Vector256<byte>.Zero).AsInt64();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CSA(out Vector256<long> h, out Vector256<long> l, ref Vector256<long> a, ref Vector256<long> b, ref Vector256<long> c)
    {
        Vector256<long> u = Avx2.Xor(a, b);
        h = Avx2.Or(Avx2.And(a, b), Avx2.And(u, c));
        l = Avx2.Xor(u, c);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Popcnt(ref Vector256<long> start, int size)
    {
        if (size == 0)
        {
            return 0;
        }
        
        Vector256<long> total = Vector256<long>.Zero;
        Vector256<long> ones = Vector256<long>.Zero;
        Vector256<long> twos = Vector256<long>.Zero;

        int limit = size - size % 4;

        if (limit >= 4)
        {
            ref Vector256<long> end = ref Unsafe.Add(ref start, limit);

            while (Unsafe.IsAddressLessThan(ref start, ref end))
            {
                CSA(out Vector256<long> twosA, out ones, ref ones, ref start, ref Unsafe.Add(ref start, 1));
                CSA(out Vector256<long> twosB, out ones, ref ones, ref Unsafe.Add(ref start, 2), ref Unsafe.Add(ref start, 3));
                CSA(out Vector256<long> fours, out twos, ref twos, ref twosA, ref twosB);

                total = Avx2.Add(total, PopcntVec(ref fours));
            
                start = ref Unsafe.Add(ref start, 4);
            }

            total = Avx2.ShiftLeftLogical(total, 2);
            total = Avx2.Add(total, Avx2.ShiftLeftLogical(PopcntVec(ref twos), 1));
            total = Avx2.Add(total, PopcntVec(ref ones));
        }
        
        ref Vector256<long> end2 = ref Unsafe.Add(ref start, size % 4);

        // Handle remaining vectors
        while (Unsafe.IsAddressLessThan(ref start, ref end2))
        {
            total = Avx2.Add(total, PopcntVec(ref start));
            start = ref Unsafe.Add(ref start, 1);
        }

        return (ulong)total.GetElement(0) +
               (ulong)total.GetElement(1) +
               (ulong)total.GetElement(2) +
               (ulong)total.GetElement(3);
    }
}
