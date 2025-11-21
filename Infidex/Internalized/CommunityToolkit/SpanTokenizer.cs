using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Infidex.Internalized.CommunityToolkit;

/// <summary>
/// A <see langword="ref"/> <see langword="struct"/> that tokenizes a given <see cref="Span{T}"/> instance.
/// </summary>
/// <typeparam name="T">The type of items to enumerate.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public ref struct SpanTokenizer<T>
    where T : IEquatable<T>
{
    /// <summary>
    /// The source <see cref="Span{T}"/> instance.
    /// </summary>
    private readonly Span<T> span;

    /// <summary>
    /// The separator item to use.
    /// </summary>
    private readonly T separator;

    /// <summary>
    /// The current initial offset.
    /// </summary>
    private int start;

    /// <summary>
    /// The current final offset.
    /// </summary>
    private int end;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpanTokenizer{T}"/> struct.
    /// </summary>
    /// <param name="span">The source <see cref="Span{T}"/> instance.</param>
    /// <param name="separator">The separator item to use.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanTokenizer(Span<T> span, T separator)
    {
        this.span = span;
        this.separator = separator;
        start = 0;
        end = -1;
    }

    /// <summary>
    /// Implements the duck-typed <see cref="IEnumerable{T}.GetEnumerator"/> method.
    /// </summary>
    /// <returns>An <see cref="SpanTokenizer{T}"/> instance targeting the current <see cref="Span{T}"/> value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly SpanTokenizer<T> GetEnumerator() => this;

    /// <summary>
    /// Implements the duck-typed <see cref="System.Collections.IEnumerator.MoveNext"/> method.
    /// </summary>
    /// <returns><see langword="true"/> whether a new element is available, <see langword="false"/> otherwise</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        int newEnd = end + 1;
        int length = span.Length;

        // Additional check if the separator is not the last character
        if (newEnd <= length)
        {
            start = newEnd;

            // Here we're inside the 'CommunityToolkit.HighPerformance.Enumerables' namespace, so the
            // 'MemoryExtensions' type from the .NET Community Toolkit would be bound instead. Because
            // want the one from the BCL (to search by value), we can use its fully qualified name.
            int index = MemoryExtensions.IndexOf(span.Slice(newEnd), separator);

            // Extract the current subsequence
            if (index >= 0)
            {
                end = newEnd + index;

                return true;
            }

            end = length;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the duck-typed <see cref="IEnumerator{T}.Current"/> property.
    /// </summary>
    public readonly Span<T> Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => span.Slice(start, end - start);
    }
}