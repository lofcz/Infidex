using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Infidex.Internalized.Roaring;

internal static class Utils
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Popcnt(ulong[] longs)
    {
        if (AdvSimd.IsSupported)
        {
            return (int)PopcntNeon.Popcnt(longs.AsSpan());
        }
        if (Avx2.IsSupported)
        {
            return (int)PopcntAvx2.Popcnt(longs.AsSpan());
        }
        if (Avx512F.IsSupported && Avx512BW.IsSupported)
        {
            return (int)PopcntAvx512.Popcnt(longs.AsSpan());
        }
        
        return Popcnt64.Popcnt(longs);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ArrayCopy(ushort[] input, int iStart, ushort[] output, int oStart, int length)
    {
        Buffer.BlockCopy(input, iStart * sizeof(ushort), output, oStart * sizeof(ushort), length * sizeof(ushort));
    }

    public static int UnionArrays(ushort[] set1, int length1, ushort[] set2, int length2, ushort[] buffer)
    {
        int pos = 0;
        int k1 = 0, k2 = 0;
        if (0 == length2)
        {
            ArrayCopy(set1, 0, buffer, 0, length1);
            return length1;
        }
        if (0 == length1)
        {
            ArrayCopy(set2, 0, buffer, 0, length2);
            return length2;
        }
        ushort s1 = set1[k1];
        ushort s2 = set2[k2];
        while (true)
        {
            int v1 = s1;
            int v2 = s2;
            if (v1 < v2)
            {
                buffer[pos++] = s1;
                ++k1;
                if (k1 >= length1)
                {
                    ArrayCopy(set2, k2, buffer, pos, length2 - k2);
                    return pos + length2 - k2;
                }
                s1 = set1[k1];
            }
            else if (v1 == v2)
            {
                buffer[pos++] = s1;
                ++k1;
                ++k2;
                if (k1 >= length1)
                {
                    ArrayCopy(set2, k2, buffer, pos, length2 - k2);
                    return pos + length2 - k2;
                }
                if (k2 >= length2)
                {
                    ArrayCopy(set1, k1, buffer, pos, length1 - k1);
                    return pos + length1 - k1;
                }
                s1 = set1[k1];
                s2 = set2[k2];
            }
            else // if (set1[k1]>set2[k2])
            {
                buffer[pos++] = s2;
                ++k2;
                if (k2 >= length2)
                {
                    ArrayCopy(set1, k1, buffer, pos, length1 - k1);
                    return pos + length1 - k1;
                }
                s2 = set2[k2];
            }
        }
    }

    public static int DifferenceArrays(ushort[] set1, int length1, ushort[] set2, int length2, ushort[] buffer)
    {
        int pos = 0;
        int k1 = 0, k2 = 0;
        if (0 == length2)
        {
            ArrayCopy(set1, 0, buffer, 0, length1);
            return length1;
        }
        if (0 == length1)
        {
            return 0;
        }
        ushort s1 = set1[k1];
        ushort s2 = set2[k2];
        while (true)
        {
            if (s1 < s2)
            {
                buffer[pos++] = s1;
                ++k1;
                if (k1 >= length1)
                {
                    break;
                }
                s1 = set1[k1];
            }
            else if (s1 == s2)
            {
                ++k1;
                ++k2;
                if (k1 >= length1)
                {
                    break;
                }
                if (k2 >= length2)
                {
                    ArrayCopy(set1, k1, buffer, pos, length1 - k1);
                    return pos + length1 - k1;
                }
                s1 = set1[k1];
                s2 = set2[k2];
            }
            else // if (val1>val2)
            {
                ++k2;
                if (k2 >= length2)
                {
                    ArrayCopy(set1, k1, buffer, pos, length1 - k1);
                    return pos + length1 - k1;
                }
                s2 = set2[k2];
            }
        }
        return pos;
    }

    public static int IntersectArrays(ReadOnlySpan<ushort> set1, ReadOnlySpan<ushort> set2, ushort[] buffer)
    {
        if (set1.Length << 6 < set2.Length)
        {
            return OneSidedGallopingIntersect2By2(set1, set2, buffer);
        }
        return set2.Length << 6 < set1.Length ? OneSidedGallopingIntersect2By2(set2, set1, buffer) : LocalIntersect2By2(set1, set2, buffer);
    }

    private static int LocalIntersect2By2(ReadOnlySpan<ushort> set1, ReadOnlySpan<ushort> set2, ushort[] buffer)
    {
        if (0 == set1.Length || 0 == set2.Length)
        {
            return 0;
        }
        int k1 = 0;
        int k2 = 0;
        int pos = 0;
        ushort s1 = set1[k1];
        ushort s2 = set2[k2];

        while (true)
        {
            int v1 = s1;
            int v2 = s2;
            while (v2 < v1)
            {
                ++k2;
                if (k2 == set2.Length)
                {
                    return pos;
                }
                s2 = set2[k2];
                v2 = s2;
            }
            if (v1 < v2)
            {
                do
                {
                    ++k1;
                    if (k1 == set1.Length)
                    {
                        return pos;
                    }
                    s1 = set1[k1];
                    v1 = s1;
                } while (v1 < v2);
            }
            else // (set2[k2] == set1[k1])
            {
                buffer[pos++] = s1;
                ++k1;
                if (k1 == set1.Length)
                {
                    break;
                }
                ++k2;
                if (k2 == set2.Length)
                {
                    break;
                }
                s1 = set1[k1];
                s2 = set2[k2];
            }
        }
        return pos;
    }

    private static int OneSidedGallopingIntersect2By2(ReadOnlySpan<ushort> smallSet, ReadOnlySpan<ushort> largeSet, ushort[] buffer)
    {
        if (0 == smallSet.Length)
        {
            return 0;
        }
        int k1 = 0;
        int k2 = 0;
        int pos = 0;
        ushort s1 = largeSet[k1];
        ushort s2 = smallSet[k2];
        while (true)
        {
            if (s1 < s2)
            {
                k1 = AdvanceUntil(largeSet, k1, s2);
                if (k1 == largeSet.Length)
                {
                    break;
                }
                s1 = largeSet[k1];
            }
            if (s2 < s1)
            {
                ++k2;
                if (k2 == smallSet.Length)
                {
                    break;
                }
                s2 = smallSet[k2];
            }
            else // (set2[k2] == set1[k1])
            {
                buffer[pos++] = s2;
                ++k2;
                if (k2 == smallSet.Length)
                {
                    break;
                }
                s2 = smallSet[k2];
                k1 = AdvanceUntil(largeSet, k1, s2);
                if (k1 == largeSet.Length)
                {
                    break;
                }
                s1 = largeSet[k1];
            }
        }
        return pos;
    }

    /// <summary>
    /// Find the smallest integer larger than pos such that array[pos]>= min. otherwise return length
    /// -> The first line is BinarySearch with pos + 1, the second line is the bitwise complement if the value can't be found
    /// </summary>
    public static int AdvanceUntil(ReadOnlySpan<ushort> span, int pos, ushort min)
    {
        int start = pos + 1; // check the next one
        if (start >= span.Length || span[start] >= min) // the simple cases
        {
            return start;
        }
        int result = span.Slice(start).BinarySearch(min);
        return (result < 0 ? ~result : result) + start;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort HighBits(int value)
    {
        return (ushort) (value >> 16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort LowBits(int value)
    {
        return (ushort) (value & 0xFFFF);
    }

    public static int XorArrays(ushort[] set1, int length1, ushort[] set2, int length2, ushort[] buffer)
    {
        int pos = 0;
        int k1 = 0, k2 = 0;
        if (0 == length2)
        {
            ArrayCopy(set1, 0, buffer, 0, length1);
            return length1;
        }
        if (0 == length1)
        {
            ArrayCopy(set2, 0, buffer, 0, length2);
            return length2;
        }
        ushort s1 = set1[k1];
        ushort s2 = set2[k2];
        while (true)
        {
            if (s1 < s2)
            {
                buffer[pos++] = s1;
                ++k1;
                if (k1 >= length1)
                {
                    ArrayCopy(set2, k2, buffer, pos, length2 - k2);
                    return pos + length2 - k2;
                }
                s1 = set1[k1];
            }
            else if (s1 == s2)
            {
                ++k1;
                ++k2;
                if (k1 >= length1)
                {
                    ArrayCopy(set2, k2, buffer, pos, length2 - k2);
                    return pos + length2 - k2;
                }
                if (k2 >= length2)
                {
                    ArrayCopy(set1, k1, buffer, pos, length1 - k1);
                    return pos + length1 - k1;
                }
                s1 = set1[k1];
                s2 = set2[k2];
            }
            else // if (val1>val2)
            {
                buffer[pos++] = s2;
                ++k2;
                if (k2 >= length2)
                {
                    ArrayCopy(set1, k1, buffer, pos, length1 - k1);
                    return pos + length1 - k1;
                }
                s2 = set2[k2];
            }
        }
    }
}
