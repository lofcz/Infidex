namespace Infidex.Core;

/// <summary>
/// Represents a search result entry with a score and document identifier.
/// </summary>
public record ScoreEntry(byte Score, long DocumentId, int? SegmentNumber = null)
{
    public override string ToString()
    {
        if (SegmentNumber.HasValue)
            return $"Score: {Score}, DocId: {DocumentId}, Segment: {SegmentNumber.Value}";
        return $"Score: {Score}, DocId: {DocumentId}";
    }
}

