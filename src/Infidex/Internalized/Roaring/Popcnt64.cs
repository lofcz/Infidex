using System.Numerics;
using System.Runtime.CompilerServices;

namespace Infidex.Internalized.Roaring;

internal static class Popcnt64
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Popcnt(ulong[] xArray)
    {
        int result = 0;
        for (int i = 0; i < xArray.Length; i++)
        {
            result += BitOperations.PopCount(xArray[i]);
        }

        return result;
    }
}
