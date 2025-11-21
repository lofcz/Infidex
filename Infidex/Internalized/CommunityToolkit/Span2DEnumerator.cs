namespace Infidex.Internalized.CommunityToolkit;

#if !NET8_0_OR_GREATER
using System;
#endif
using System.Runtime.CompilerServices;
#if NETSTANDARD2_1_OR_GREATER && !NET8_0_OR_GREATER
using System.Runtime.InteropServices;
#endif

/// <inheritdoc/>
partial struct Span2D<T>
{
    /// <summary>
    /// Gets an enumerable that traverses items in a specified row.
    /// </summary>
    /// <param name="row">The target row to enumerate within the current <see cref="Span2D{T}"/> instance.</param>
    /// <returns>A <see cref="RefEnumerable{T}"/> with target items to enumerate.</returns>
    /// <remarks>The returned <see cref="RefEnumerable{T}"/> value shouldn't be used directly: use this extension in a <see langword="foreach"/> loop.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RefEnumerable<T> GetRow(int row)
    {
        if ((uint)row >= Height)
        {
            ThrowHelper.ThrowArgumentOutOfRangeExceptionForRow();
        }

        nint startIndex = (nint)(uint)Stride * (nint)(uint)row;
        ref T r0 = ref DangerousGetReference();
        ref T r1 = ref Unsafe.Add(ref r0, startIndex);

#if NETSTANDARD2_1_OR_GREATER
        return new RefEnumerable<T>(ref r1, Width, 1);
#else
        IntPtr offset = RuntimeHelpers.GetObjectDataOrReferenceByteOffset(this.Instance, ref r1);

        return new(this.Instance, offset, this.width, 1);
#endif
    }

    /// <summary>
    /// Gets an enumerable that traverses items in a specified column.
    /// </summary>
    /// <param name="column">The target column to enumerate within the current <see cref="Span2D{T}"/> instance.</param>
    /// <returns>A <see cref="RefEnumerable{T}"/> with target items to enumerate.</returns>
    /// <remarks>The returned <see cref="RefEnumerable{T}"/> value shouldn't be used directly: use this extension in a <see langword="foreach"/> loop.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RefEnumerable<T> GetColumn(int column)
    {
        if ((uint)column >= Width)
        {
            ThrowHelper.ThrowArgumentOutOfRangeExceptionForColumn();
        }

        ref T r0 = ref DangerousGetReference();
        ref T r1 = ref Unsafe.Add(ref r0, (nint)(uint)column);

#if NETSTANDARD2_1_OR_GREATER
        return new RefEnumerable<T>(ref r1, Height, Stride);
#else
        IntPtr offset = RuntimeHelpers.GetObjectDataOrReferenceByteOffset(this.Instance, ref r1);

        return new(this.Instance, offset, Height, this.Stride);
#endif
    }

    /// <summary>
    /// Returns an enumerator for the current <see cref="Span2D{T}"/> instance.
    /// </summary>
    /// <returns>
    /// An enumerator that can be used to traverse the items in the current <see cref="Span2D{T}"/> instance
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Enumerator GetEnumerator() => new Enumerator(this);

    /// <summary>
    /// Provides an enumerator for the elements of a <see cref="Span2D{T}"/> instance.
    /// </summary>
    public ref struct Enumerator
    {
#if NET8_0_OR_GREATER
        /// <summary>
        /// The <typeparamref name="T"/> reference for the <see cref="Span2D{T}"/> instance.
        /// </summary>
        private readonly ref T reference;

        /// <summary>
        /// The height of the specified 2D region.
        /// </summary>
        private readonly int height;
#elif NETSTANDARD2_1_OR_GREATER
        /// <summary>
        /// The <see cref="Span{T}"/> instance pointing to the first item in the target memory area.
        /// </summary>
        /// <remarks>Just like in <see cref="Span2D{T}"/>, the length is the height of the 2D region.</remarks>
        private readonly Span<T> span;
#else
        /// <summary>
        /// The target <see cref="object"/> instance, if present.
        /// </summary>
        private readonly object? instance;

        /// <summary>
        /// The initial byte offset within <see cref="instance"/>.
        /// </summary>
        private readonly nint offset;

        /// <summary>
        /// The height of the specified 2D region.
        /// </summary>
        private readonly int height;
#endif

        /// <summary>
        /// The width of the specified 2D region.
        /// </summary>
        private readonly int width;

        /// <summary>
        /// The stride of the specified 2D region.
        /// </summary>
        private readonly int stride;

        /// <summary>
        /// The current horizontal offset.
        /// </summary>
        private int x;

        /// <summary>
        /// The current vertical offset.
        /// </summary>
        private int y;

        /// <summary>
        /// Initializes a new instance of the <see cref="Enumerator"/> struct.
        /// </summary>
        /// <param name="span">The target <see cref="Span2D{T}"/> instance to enumerate.</param>
        internal Enumerator(Span2D<T> span)
        {
#if NET8_0_OR_GREATER
            reference = ref span.reference;
            height = span.height;
#elif NETSTANDARD2_1_OR_GREATER
            this.span = span.span;
#else
            this.instance = span.Instance;
            this.offset = span.Offset;
            this.height = span.height;
#endif
            width = span.width;
            stride = span.Stride;
            x = -1;
            y = 0;
        }

        /// <summary>
        /// Implements the duck-typed <see cref="System.Collections.IEnumerator.MoveNext"/> method.
        /// </summary>
        /// <returns><see langword="true"/> whether a new element is available, <see langword="false"/> otherwise</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            int x = this.x + 1;

            // Horizontal move, within range
            if (x < width)
            {
                this.x = x;

                return true;
            }

            // We reached the end of a row and there is at least
            // another row available: wrap to a new line and continue.
            this.x = 0;

#if NET8_0_OR_GREATER
            return ++y < height;
#elif NETSTANDARD2_1_OR_GREATER
            return ++this.y < this.span.Length;
#else
            return ++this.y < this.height;
#endif
        }

        /// <summary>
        /// Gets the duck-typed <see cref="System.Collections.Generic.IEnumerator{T}.Current"/> property.
        /// </summary>
        public readonly ref T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if NET8_0_OR_GREATER
                ref T r0 = ref reference;
#elif NETSTANDARD2_1_OR_GREATER
                ref T r0 = ref MemoryMarshal.GetReference(this.span);
#else
                ref T r0 = ref RuntimeHelpers.GetObjectDataAtOffsetOrPointerReference<T>(this.instance, this.offset);
#endif
                nint index = ((nint)(uint)y * (nint)(uint)stride) + (nint)(uint)x;

                return ref Unsafe.Add(ref r0, index);
            }
        }
    }
}