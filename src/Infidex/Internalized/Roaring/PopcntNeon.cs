using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace Infidex.Internalized.Roaring;

internal static class PopcntNeon
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Popcnt(ReadOnlySpan<ulong> data)
    {
        ref Vector128<byte> start = ref Unsafe.As<ulong, Vector128<byte>>(ref MemoryMarshal.GetReference(data));
        
        const int VECTOR_SIZE = 2;
        const int UNROLL_FACTOR = 2;
        
        ulong cnt = 0;
        
        int numberOfVectors = data.Length / VECTOR_SIZE;
        int numberOfUnrolledIterations = numberOfVectors / UNROLL_FACTOR * UNROLL_FACTOR;
        ref Vector128<byte> end = ref Unsafe.Add(ref start, numberOfUnrolledIterations);

        while (Unsafe.IsAddressLessThan(ref start, ref end))
        {
            // Don't process more than 31 elements
            ref Vector128<byte> end2 = ref Unsafe.Add(ref start, Math.Min(31, (int)(Unsafe.ByteOffset(ref start, ref end) / 16)));
            
            Vector128<byte> t0 = Vector128<byte>.Zero;
            Vector128<byte> t1 = Vector128<byte>.Zero;
            
            while (Unsafe.IsAddressLessThan(ref start, ref end2)) {
                t0 = AdvSimd.Add(t0, AdvSimd.PopCount(start));
                t1 = AdvSimd.Add(t1, AdvSimd.PopCount(Unsafe.Add(ref start, 1)));
                
                start = ref Unsafe.Add(ref start, UNROLL_FACTOR);
            }

            Vector128<ulong> sum = AdvSimd.AddPairwiseWidening(AdvSimd.AddPairwiseWidening(AdvSimd.AddPairwiseWidening(t0)));
            sum = AdvSimd.AddPairwiseWideningAndAdd(sum, AdvSimd.AddPairwiseWidening(AdvSimd.AddPairwiseWidening(t1)));
            cnt += sum.GetElement(0) + sum.GetElement(1);
        }
        
        int remainingUlongs = data.Length - numberOfUnrolledIterations * VECTOR_SIZE;
        ReadOnlySpan<ulong> remainingData = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<Vector128<byte>, ulong>(ref start), remainingUlongs);
        
        // Handle remaining ulongs
        for (int j = 0; j < remainingData.Length; j++)
        {
            cnt += (ulong)BitOperations.PopCount(remainingData[j]);
        }

        return cnt;
    }
}
