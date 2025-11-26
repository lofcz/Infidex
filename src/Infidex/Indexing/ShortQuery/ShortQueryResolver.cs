using System.Runtime.CompilerServices;
using Infidex.Core;

namespace Infidex.Indexing.ShortQuery;

/// <summary>
/// Resolves short queries (1-3 characters) using the pre-built PositionalPrefixIndex.
/// Achieves near O(1) lookup time with smart early termination and ranking.
/// </summary>
internal sealed class ShortQueryResolver
{
    private readonly PositionalPrefixIndex _prefixIndex;
    private readonly DocumentCollection _documents;
    private readonly char[] _delimiters;
    
    public ShortQueryResolver(
        PositionalPrefixIndex prefixIndex, 
        DocumentCollection documents,
        char[]? delimiters = null)
    {
        _prefixIndex = prefixIndex;
        _documents = documents;
        _delimiters = delimiters ?? [' '];
    }
    
    /// <summary>
    /// Resolves a short query and returns scored results.
    /// </summary>
    /// <param name="query">The query text (1-3 chars, already lowercased).</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <returns>Array of score entries sorted by relevance.</returns>
    public ScoreEntry[] Resolve(ReadOnlySpan<char> query, int maxResults = int.MaxValue)
    {
        if (query.IsEmpty || query.Length > _prefixIndex.MaxPrefixLength)
            return [];
        
        PrefixPostingList? postingList = _prefixIndex.GetPostingList(query);
        if (postingList == null || postingList.Count == 0)
            return [];
        
        // Group postings by document and calculate scores
        Dictionary<int, DocumentScore> docScores = new Dictionary<int, DocumentScore>();
        
        foreach (ref readonly PrefixPosting posting in postingList.Postings)
        {
            int docId = posting.DocumentId;
            
            if (!docScores.TryGetValue(docId, out DocumentScore score))
            {
                Document? doc = _documents.GetDocument(docId);
                if (doc == null || doc.Deleted)
                    continue;
                
                score = new DocumentScore { DocumentKey = doc.DocumentKey };
                docScores[docId] = score;
            }
            
            // Update score based on position information
            score.Occurrences++;
            if (posting.IsWordStart)
            {
                score.WordStartCount++;
                if (posting.Position == 0)
                    score.HasFirstPosition = true;
                if (!score.HasWordStart || posting.Position < score.FirstWordStartPosition)
                {
                    score.HasWordStart = true;
                    score.FirstWordStartPosition = posting.Position;
                }
            }
        }
        
        // Convert to final scores with precedence calculation
        List<ScoreEntry> results = new List<ScoreEntry>(docScores.Count);
        
        foreach ((int docId, DocumentScore score) in docScores)
        {
            Document? doc = _documents.GetDocument(docId);
            if (doc == null || doc.Deleted)
                continue;
            
            // Calculate precedence and base score
            ushort finalScore = CalculateFinalScore(query, doc, score);
            results.Add(new ScoreEntry(finalScore, score.DocumentKey));
        }
        
        // Sort by score descending
        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        
        // Limit results
        if (results.Count > maxResults)
            results.RemoveRange(maxResults, results.Count - maxResults);
        
        return results.ToArray();
    }
    
    /// <summary>
    /// Resolves a single character query with optimized scoring.
    /// </summary>
    public ScoreEntry[] ResolveSingleChar(char ch, int maxResults = int.MaxValue)
    {
        Span<char> query = stackalloc char[1];
        query[0] = char.ToLowerInvariant(ch);
        return Resolve(query, maxResults);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort CalculateFinalScore(ReadOnlySpan<char> query, Document doc, DocumentScore score)
    {
        // Precedence bits (high byte):
        // Bit 7 (128): Has word start match
        // Bit 6 (64): First position match
        // Bit 5 (32): Any exact token match
        // Bit 4 (16): First token exact
        // Bit 3 (8): Title equals query
        // Bits 0-2: Reserved
        
        int precedence = 0;
        
        if (score.HasWordStart)
        {
            precedence |= 128;
            if (score.FirstWordStartPosition == 0)
                precedence |= 64;
        }
        
        // Check for exact token match
        string titleLower = doc.IndexedText?.ToLowerInvariant() ?? "";
        string queryStr = query.ToString();
        string[] tokens = titleLower.Split(_delimiters, StringSplitOptions.RemoveEmptyEntries);
        
        bool anyExact = false;
        bool firstExact = false;
        
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == queryStr)
            {
                anyExact = true;
                if (i == 0)
                    firstExact = true;
                break;
            }
        }
        
        if (anyExact) precedence |= 32;
        if (firstExact) precedence |= 16;
        if (titleLower.Trim() == queryStr) precedence |= 8;
        if (tokens.Length <= 3) precedence |= 32; // Boost short titles
        
        // Base score (low byte): position-based + density
        byte baseScore;
        if (score.HasWordStart)
        {
            int posComponent = 255 - Math.Min(score.FirstWordStartPosition * 16, 240);
            int densityComponent = Math.Min(score.WordStartCount * 8, 32);
            baseScore = (byte)Math.Clamp(posComponent + densityComponent, 0, 255);
        }
        else
        {
            int densityComponent = Math.Min(score.Occurrences * 4, 200);
            baseScore = (byte)Math.Max(1, densityComponent);
        }
        
        return (ushort)((precedence << 8) | baseScore);
    }
    
    /// <summary>
    /// Helper class to accumulate document scores during resolution.
    /// </summary>
    private sealed class DocumentScore
    {
        public long DocumentKey;
        public int Occurrences;
        public int WordStartCount;
        public bool HasWordStart;
        public bool HasFirstPosition;
        public int FirstWordStartPosition = int.MaxValue;
    }
}


