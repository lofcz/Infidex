using System.Collections.Concurrent;
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
    
    // Precomputed champion lists for each prefix (1-3 chars).
    // Key: prefix string, Value: top-N ScoreEntries sorted by score desc.
    private readonly Dictionary<string, ScoreEntry[]> _championLists;
    
    private const int ChampionListSize = 64;
    
    public ShortQueryResolver(
        PositionalPrefixIndex prefixIndex, 
        DocumentCollection documents,
        char[]? delimiters = null)
    {
        _prefixIndex = prefixIndex;
        _documents = documents;
        _delimiters = delimiters ?? [' '];
        _championLists = BuildChampionLists();
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

        // Fast path: if we have a champion list for this prefix and it already
        // provides at least maxResults entries, we can satisfy the query directly
        // from the precomputed list in O(1).
        if (TryGetChampions(query, maxResults, out ScoreEntry[] championResults))
        {
            return championResults;
        }
        
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
    /// Builds champion lists (top-N documents) for each prefix using the same
    /// scoring logic as Resolve. This runs once at index finalization time.
    /// Work is parallelized across prefixes to take advantage of multiple cores.
    /// </summary>
    private Dictionary<string, ScoreEntry[]> BuildChampionLists()
    {
        // Materialize all prefixes so we can safely parallelize without
        // touching the underlying index concurrently.
        (string Prefix, PrefixPostingList List)[] prefixes =
            _prefixIndex.GetAllPrefixes().ToArray();

        if (prefixes.Length == 0)
            return new Dictionary<string, ScoreEntry[]>(StringComparer.Ordinal);

        ConcurrentDictionary<string, ScoreEntry[]> result = new ConcurrentDictionary<string, ScoreEntry[]>(StringComparer.Ordinal);

        Parallel.ForEach(
            prefixes,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            entry =>
            {
                string prefix = entry.Prefix;
                PrefixPostingList postingList = entry.List;

                if (string.IsNullOrEmpty(prefix) || postingList.Count == 0)
                    return;

                ReadOnlySpan<char> querySpan = prefix.AsSpan();

                // Group postings by document and calculate scores (same logic as Resolve),
                // but using only local state to avoid contention.
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

                if (docScores.Count == 0)
                    return;

                List<ScoreEntry> scores = new List<ScoreEntry>(docScores.Count);

                foreach ((int docId, DocumentScore score) in docScores)
                {
                    Document? doc = _documents.GetDocument(docId);
                    if (doc == null || doc.Deleted)
                        continue;

                    ushort finalScore = CalculateFinalScore(querySpan, doc, score);
                    scores.Add(new ScoreEntry(finalScore, score.DocumentKey));
                }

                if (scores.Count == 0)
                    return;

                // Sort by score descending and take top-N
                scores.Sort((a, b) => b.Score.CompareTo(a.Score));

                if (scores.Count > ChampionListSize)
                    scores.RemoveRange(ChampionListSize, scores.Count - ChampionListSize);

                result[prefix] = scores.ToArray();
            });

        return new Dictionary<string, ScoreEntry[]>(result, StringComparer.Ordinal);
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
    
    /// <summary>
    /// Attempts to satisfy a prefix query directly from the champion lists.
    /// Returns true if at least <paramref name="maxResults"/> entries are available.
    /// </summary>
    public bool TryGetChampions(ReadOnlySpan<char> prefix, int maxResults, out ScoreEntry[] results)
    {
        results = [];
        
        if (maxResults <= 0)
            return false;
        
        if (prefix.IsEmpty || prefix.Length > _prefixIndex.MaxPrefixLength)
            return false;
        
        string key = prefix.ToString();
        if (!_championLists.TryGetValue(key, out ScoreEntry[]? champions) || champions.Length == 0)
            return false;
        
        if (champions.Length < maxResults)
            return false;
        
        if (champions.Length == maxResults)
        {
            results = champions;
        }
        else
        {
            results = champions[..maxResults];
        }
        
        return true;
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


