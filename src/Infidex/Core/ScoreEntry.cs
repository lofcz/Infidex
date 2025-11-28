namespace Infidex.Core;

/// <summary>
/// Represents a search result entry with a score and document identifier.
/// </summary>
public struct ScoreEntry : IComparable<ScoreEntry>
{
    public float Score { get; set; }
    public long DocumentId { get; set; }
    public byte Tiebreaker { get; set; }
    public int? SegmentNumber { get; set; }
    public int MatchedTermCount { get; set; }
    public int LongestSequence { get; set; }

    public ScoreEntry(float score, long documentId, byte tiebreaker = 0, int? segmentNumber = null)
    {
        Score = score;
        DocumentId = documentId;
        Tiebreaker = tiebreaker;
        SegmentNumber = segmentNumber;
        MatchedTermCount = 0;
        LongestSequence = 0;
    }

    public int CompareTo(ScoreEntry other)
    {
        // Primary: Score
        int scoreCmp = Score.CompareTo(other.Score);
        if (scoreCmp != 0) return scoreCmp;

        // Secondary: Tiebreaker
        int tieCmp = Tiebreaker.CompareTo(other.Tiebreaker);
        return tieCmp != 0 ? tieCmp :
            // Tertiary: DocumentId (Deterministic tie-breaking for stable sort)
            other.DocumentId.CompareTo(DocumentId);
    }

    public override string ToString()
    {
        return SegmentNumber.HasValue ? $"Score: {Score:F4}, DocId: {DocumentId}, Tie: {Tiebreaker}, Seg: {SegmentNumber.Value}" : $"Score: {Score:F4}, DocId: {DocumentId}, Tie: {Tiebreaker}";
    }
}
