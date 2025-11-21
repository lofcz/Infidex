using System.Runtime.InteropServices;
using Infidex.Internalized.CommunityToolkit;

namespace Infidex.Utilities;

/// <summary>
/// Provides native memory allocation for high-performance operations.
/// Uses unmanaged memory to avoid GC pressure during scoring.
/// </summary>
internal static class SpanAlloc
{
    /// <summary>
    /// Allocates a zeroed unmanaged memory block and returns it as a Span.
    /// </summary>
    /// <param name="size">Number of bytes to allocate</param>
    /// <param name="pointer">Output pointer for later deallocation</param>
    /// <returns>Span view of the allocated memory</returns>
    public static unsafe Span<byte> Alloc(int size, out long pointer)
    {
        void* ptr = NativeMemory.AllocZeroed((nuint)size);
        
        if (ptr == null)
        {
            throw new OutOfMemoryException($"Failed to allocate {size} bytes of native memory");
        }
        
        pointer = (long)ptr;
        return new Span<byte>(ptr, size);
    }
    
    /// <summary>
    /// Allocates a 2D unmanaged memory block and returns it as a Span2D.
    /// </summary>
    /// <param name="rows">Number of rows</param>
    /// <param name="cols">Number of columns</param>
    /// <param name="pointer">Output pointer for later deallocation</param>
    /// <returns>Span2D view of the allocated memory</returns>
    public static unsafe Span2D<byte> Alloc(int rows, int cols, out long pointer)
    {
        void* ptr = NativeMemory.AllocZeroed((nuint)(rows * cols));
        pointer = (long)ptr;
        return new Span2D<byte>(ptr, rows, cols, 0);
    }
    
    /// <summary>
    /// Allocates a 2D unmanaged memory block and returns it as a Span2D (alias for Alloc overload).
    /// </summary>
    public static Span2D<byte> Alloc2D(int rows, int cols, out long pointer)
    {
        return Alloc(rows, cols, out pointer);
    }
    
    /// <summary>
    /// Frees previously allocated native memory.
    /// </summary>
    /// <param name="pointer">Pointer returned from Alloc</param>
    public static unsafe void Free(long pointer)
    {
        NativeMemory.Free((void*)pointer);
    }
}

