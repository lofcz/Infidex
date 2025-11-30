namespace Infidex.Indexing;

/// <summary>
/// Constants for PostingsEnum.
/// </summary>
public static class PostingsEnumConstants
{
    public const int NO_MORE_DOCS = int.MaxValue;
}

/// <summary>
/// Iterates over postings (documents and frequencies).
/// Similar to Lucene's PostingsEnum/DocIdSetIterator.
/// </summary>
public interface IPostingsEnum
{
    /// <summary>
    /// Current document ID. -1 if not started, NO_MORE_DOCS if exhausted.
    /// </summary>
    int DocID { get; }

    /// <summary>
    /// Frequency of the term in the current document.
    /// </summary>
    float Freq { get; }

    /// <summary>
    /// Advances to the next document.
    /// </summary>
    /// <returns>The new DocID, or NO_MORE_DOCS if finished.</returns>
    int NextDoc();

    /// <summary>
    /// Advances to the first document >= target.
    /// </summary>
    /// <returns>The new DocID (>= target), or NO_MORE_DOCS if finished.</returns>
    int Advance(int target);
    
    /// <summary>
    /// Returns the estimated cost of this iterator (e.g. number of docs).
    /// </summary>
    long Cost();
}

// Legacy abstract class for backward compatibility if needed, 
// but we prefer interface for struct support.
// We remove the abstract class to force migration.
