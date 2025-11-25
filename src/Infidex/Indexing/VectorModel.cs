using Infidex.Api;
using Infidex.Core;
using Infidex.Tokenization;
using Infidex.Utilities;
using Infidex.Internalized.CommunityToolkit;

namespace Infidex.Indexing;

/// <summary>
/// Implements TF-IDF vector space model for relevancy ranking.
/// This is Stage 1 of the search process.
/// </summary>
public class VectorModel
{
    private readonly Tokenizer _tokenizer;
    private readonly TermCollection _termCollection;
    private readonly DocumentCollection _documents;
    private readonly int _stopTermLimit;
    private readonly float[] _fieldWeights;
    
    // BM25+ parameters and precomputed document statistics
    // See: Trotman et al., "Improvements to BM25 and Language Models Examined"
    private readonly float _bm25K1 = 1.2f;
    private readonly float _bm25B  = 0.75f;
    private readonly float _bm25Delta = 1.0f;
    private float[]? _docLengths;
    private float _avgDocLength;
    
    // Trie for O(|prefix|) term lookups - used by short query fast path
    private TermPrefixTrie? _termPrefixTrie;
    
    // Score-decomposed trie for O(|p| + k log k) top-k retrieval
    private ScoreDecomposedTrie? _scoreDecomposedTrie;
    
    // Depth-first fuzzy search for efficient fuzzy autocomplete
    private DepthFirstFuzzySearch? _fuzzySearchIndex;
    
    // Track terms added since last trie update for incremental building
    private readonly List<Term> _pendingTrieTerms = new();
    
    public Tokenizer Tokenizer => _tokenizer;
    
    /// <summary>
    /// Trie for fast O(|prefix|) term lookups. Built during BuildInvertedLists.
    /// </summary>
    internal TermPrefixTrie? TermPrefixTrie => _termPrefixTrie;
    
    /// <summary>
    /// Event fired when indexing progress changes (0-100%)
    /// </summary>
    public event EventHandler<int>? ProgressChanged;
    
    /// <summary>
    /// When enabled, Search will emit detailed debug information about
    /// per-term TF-IDF contributions and accumulated document scores.
    /// Intended for analysis/parity work, not for production use.
    /// </summary>
    public bool EnableDebugLogging { get; set; }
    
    public VectorModel(Tokenizer tokenizer, int stopTermLimit = 1_250_000, float[]? fieldWeights = null)
    {
        _tokenizer = tokenizer;
        _stopTermLimit = stopTermLimit;
        _termCollection = new TermCollection();
        _documents = new DocumentCollection();
        _fieldWeights = fieldWeights ?? ConfigurationParameters.DefaultFieldWeights;
    }
    
    /// <summary>
        /// Indexes a single document and returns the stored document (with internal Id).
        /// Uses streaming approach with multi-field support and field weights.
        /// Matches the reference implementation's indexing flow.
        /// </summary>
        public Document IndexDocument(Document document)
        {
            // Add document to collection and obtain its internal index
            Document doc = _documents.AddDocument(document);
            
            // Tokenize the text - pass segment continuation flag
            bool isSegmentContinuation = doc.SegmentNumber > 0;
            
            // Get searchable text from fields with boundary markers
            (ushort Position, byte WeightIndex)[] fieldBoundaries = 
                document.Fields.GetSearchableTexts('§', out string concatenatedText);
            
            // Store the concatenated text for later reference
            doc.IndexedText = concatenatedText;
            
            // Match original behavior: apply case normalization before indexing
            string text = concatenatedText.ToLowerInvariant();
            List<Shingle> shingles = _tokenizer.TokenizeForIndexing(text, isSegmentContinuation);
            
            // Stream tokens directly to the inverted index with field weight application
            foreach (Shingle shingle in shingles)
            {
                // Determine which field this token belongs to based on its position
                float fieldWeight = DetermineFieldWeight(shingle.Position, fieldBoundaries);
                
                // Get or create term and increment global document frequency counter
                Term term = _termCollection.CountTermUsage(shingle.Text, _stopTermLimit, forFastInsert: false, out bool isNewTerm);
                
                // Track new terms for incremental trie updates
                if (isNewTerm)
                {
                    _pendingTrieTerms.Add(term);
                }
                
                // Add this occurrence to the term's posting list with field weight
                // removeDuplicates flag: set to true for segment continuations to avoid
                // counting the same token multiple times across segment boundaries
                term.FirstCycleAdd(doc.Id, _stopTermLimit, removeDuplicates: isSegmentContinuation, fieldWeight);
            }

            return doc;
        }
    
    /// <summary>
    /// Determines the field weight for a token based on its position in the concatenated text.
    /// </summary>
    private float DetermineFieldWeight(int tokenPosition, (ushort Position, byte WeightIndex)[] fieldBoundaries)
    {
        if (fieldBoundaries.Length == 0)
            return 1.0f; // Default weight if no fields
        
        // Find the field that contains this token position
        // Field boundaries are sorted by position, so we find the last boundary before tokenPosition
        byte weightIndex = 0; // Default to first field's weight
        
        for (int i = 0; i < fieldBoundaries.Length; i++)
        {
            if (fieldBoundaries[i].Position <= tokenPosition)
            {
                weightIndex = fieldBoundaries[i].WeightIndex;
            }
            else
            {
                break; // We've gone past the token position
            }
        }
        
        // weightIndex is 0=High, 1=Med, 2=Low, which matches our _fieldWeights array indices
        if (weightIndex < _fieldWeights.Length)
            return _fieldWeights[weightIndex];
        
        return 1.0f; // Fallback
    }
    
    /// <summary>
    /// Finalizes the inverted index after all documents have been indexed.
    /// For the BM25+ backbone this computes per-document lengths and the
    /// global average document length, which are then used at query time.
    /// </summary>
    public void BuildInvertedLists(
        int batchDelayMs = -1, 
        int batchSize = 0, 
        CancellationToken cancellationToken = default)
    {
        int totalDocs = _documents.Count;
        _docLengths = new float[totalDocs];
        _avgDocLength = 0f;

        int termCount = 0;
        int totalTerms = _termCollection.Count;

        // Single pass over all postings: accumulate field‑weighted term
        // frequencies into per-document lengths. We intentionally keep
        // the raw per-document term frequencies in the postings lists
        // and compute BM25+ scores on the fly at query time.
        foreach (Term term in _termCollection.GetAllTerms())
        {
            if (++termCount % 10 == 0 && cancellationToken.IsCancellationRequested)
                return;

            if (batchDelayMs >= 0 && batchSize > 0 && termCount % batchSize == 0)
                Thread.Sleep(batchDelayMs);

            List<int>? docIds = term.GetDocumentIds();
            List<byte>? weights = term.GetWeights();

            if (docIds == null || weights == null)
                continue;

            int postings = docIds.Count;
            for (int i = 0; i < postings; i++)
            {
                int internalId = docIds[i];
                if ((uint)internalId >= (uint)totalDocs)
                    continue;

                // Interpret the stored byte as (field‑weighted) term frequency.
                // This effectively gives us a BM25F‑style field weighting where
                // fields with higher importance contributed larger TF values.
                _docLengths[internalId] += weights[i];
            }

            // Report progress (0‑100%) based on term pass
            if (termCount % 100 == 0)
            {
                ProgressChanged?.Invoke(this, termCount * 100 / Math.Max(totalTerms, 1));
            }
        }

        // Compute average document length for BM25+ normalization
        float totalLength = 0f;
        for (int i = 0; i < _docLengths.Length; i++)
        {
            totalLength += _docLengths[i];
        }

        _avgDocLength = totalDocs > 0 ? totalLength / totalDocs : 0f;

        ProgressChanged?.Invoke(this, 100);
    }
    
    /// <summary>
    /// Builds or updates the term prefix trie for O(|prefix|) lookups in short query path.
    /// On first call: builds from all terms - O(total terms).
    /// On subsequent calls: incrementally adds only new terms - O(new terms).
    /// </summary>
    internal void BuildTermPrefixTrie()
    {
        var existingTrie = _termPrefixTrie;
        
        if (existingTrie == null)
        {
            // First build: create trie with all existing terms
            var trie = new TermPrefixTrie();
            foreach (Term term in _termCollection.GetAllTerms())
            {
                trie.Add(term);
            }
            _pendingTrieTerms.Clear(); // Clear pending since we built from all terms
            _termPrefixTrie = trie;
        }
        else if (_pendingTrieTerms.Count > 0)
        {
            // Incremental update: only add new terms - O(new terms) not O(all terms)
            foreach (Term term in _pendingTrieTerms)
            {
                existingTrie.Add(term);
            }
            _pendingTrieTerms.Clear();
        }
        // else: no new terms, trie is already up-to-date
    }
    
    /// <summary>
    /// Builds the score-decomposed trie for O(|p| + k log k) top-k retrieval.
    /// Terms are sorted by IDF-weighted score (BM25+ style) to maintain heap property.
    /// 
    /// Mathematical foundation: Terms with higher IDF carry more information and
    /// should be prioritized. The score-decomposed structure enables efficient
    /// enumeration in score order without full traversal.
    /// </summary>
    internal void BuildScoreDecomposedTrie()
    {
        int totalDocs = _documents.Count;
        if (totalDocs == 0)
            return;
        
        // Collect all terms with their IDF-based scores
        var termsWithScores = new List<(string term, float score, Term termObj)>();
        
        foreach (Term term in _termCollection.GetAllTerms())
        {
            if (term.Text == null || term.DocumentFrequency <= 0)
                continue;
            
            // Skip stop terms
            if (term.DocumentFrequency > _stopTermLimit)
                continue;
            
            // Compute IDF-based score for ordering
            float idf = Metrics.InformationTheoreticScoring.ComputeIdf(totalDocs, term.DocumentFrequency);
            float score = idf * term.DocumentFrequency; // Weight by coverage
            
            termsWithScores.Add((term.Text, score, term));
        }
        
        // Sort by score descending for optimal trie construction
        termsWithScores.Sort((a, b) => b.score.CompareTo(a.score));
        
        // Build trie from sorted terms
        var trie = new ScoreDecomposedTrie();
        trie.BuildFromSorted(termsWithScores.Select(t => (t.term, t.score, (Term?)t.termObj)));
        
        _scoreDecomposedTrie = trie;
    }
    
    /// <summary>
    /// Builds the depth-first fuzzy search index for efficient fuzzy autocomplete.
    /// Provides 5-10x speedup over breadth-first approaches.
    /// </summary>
    internal void BuildFuzzySearchIndex()
    {
        int totalDocs = _documents.Count;
        if (totalDocs == 0)
            return;
        
        var fuzzyIndex = new DepthFirstFuzzySearch();
        
        foreach (Term term in _termCollection.GetAllTerms())
        {
            if (term.Text == null || term.DocumentFrequency <= 0)
                continue;
            
            // Skip stop terms
            if (term.DocumentFrequency > _stopTermLimit)
                continue;
            
            // Score based on IDF - rare terms get higher scores
            float idf = Metrics.InformationTheoreticScoring.ComputeIdf(totalDocs, term.DocumentFrequency);
            
            fuzzyIndex.Add(term.Text, idf, term);
        }
        
        _fuzzySearchIndex = fuzzyIndex;
    }
    
    /// <summary>
    /// Score-decomposed trie for O(|p| + k log k) top-k term retrieval.
    /// Built during BuildScoreDecomposedTrie.
    /// </summary>
    internal ScoreDecomposedTrie? ScoreDecomposedTrie => _scoreDecomposedTrie;
    
    /// <summary>
    /// Depth-first fuzzy search index for efficient fuzzy autocomplete.
    /// Built during BuildFuzzySearchIndex.
    /// </summary>
    internal DepthFirstFuzzySearch? FuzzySearchIndex => _fuzzySearchIndex;
    
    /// <summary>
    /// Legacy method for backward compatibility
    /// </summary>
    public void CalculateWeights()
    {
        BuildInvertedLists();
    }
    
    /// <summary>
    /// Fast insert of a single document without full reindexing
    /// </summary>
    public void FastInsert(string text, int documentIndex)
    {
        // Tokenize new document
        Shingle[] shingles = _tokenizer.TokenizeForSearch(text, out Dictionary<string, Shingle> dict, false);
        
        List<Term> terms = [];
        foreach (Shingle shingle in shingles)
        {
            Term term = _termCollection.CountTermUsage(
                shingle.Text, 
                _stopTermLimit, 
                forFastInsert: true);
            term.QueryOccurrences = (byte)shingle.Occurrences;
            terms.Add(term);
        }
        
        // Calculate query-style weights for new document
        byte[] weights = CalculateQueryWeights(terms);
        
        // Add to existing terms
        for (int i = 0; i < terms.Count; i++)
        {
            terms[i].AddForFastInsert(weights[i], documentIndex);
        }
    }
    
    /// <summary>
    /// Searches for documents matching the query using a BM25+ style scoring
    /// function over the indexed n‑gram/word terms.
    /// </summary>
    /// <param name="queryText">The search query</param>
    /// <param name="bestSegments">Optional 2D array to track best-scoring segments per document (default is empty)</param>
    /// <param name="queryIndex">Column index in bestSegments for multi-field search (default 0)</param>
    /// <summary>
    /// MaxScore algorithm parameters computed per-term.
    /// Used for exact early termination without heuristics.
    /// </summary>
    private readonly struct TermScoreInfo
    {
        public readonly Term Term;
        public readonly float Idf;
        public readonly float MaxScore; // Maximum possible BM25+ contribution from this term
        
        public TermScoreInfo(Term term, float idf, float maxScore)
        {
            Term = term;
            Idf = idf;
            MaxScore = maxScore;
        }
    }
    
    internal ScoreArray Search(string queryText, Span2D<byte> bestSegments = default, int queryIndex = 0)
    {
        return SearchWithMaxScore(queryText, int.MaxValue, bestSegments, queryIndex);
    }
    
    /// <summary>
    /// Searches for documents using the MaxScore algorithm for early termination.
    /// 
    /// MaxScore (Turtle &amp; Flood, 1995; Ding &amp; Suel, 2011) is mathematically exact:
    /// - Computes tight upper bounds on per-term score contributions
    /// - Maintains threshold θ = K-th best score seen so far
    /// - Skips documents that cannot possibly enter top-K
    /// - Zero tuning constants - purely algorithmic optimization
    /// 
    /// For query "the matrix":
    /// - "matrix" has low DF → high IDF → processed fully
    /// - "the" has high DF → low IDF → skipped for docs where current score + maxScore(the) &lt; θ
    /// </summary>
    internal ScoreArray SearchWithMaxScore(string queryText, int topK, Span2D<byte> bestSegments = default, int queryIndex = 0)
    {
        ScoreArray scoreArray = new ScoreArray();
        
        // Tokenize query
        Shingle[] queryShingles = _tokenizer.TokenizeForSearch(queryText, out Dictionary<string, Shingle> shingleDict, false);
        
        // Collect query terms
        List<Term> queryTerms = [];
        
        foreach (Shingle shingle in queryShingles)
        {
            Term? term = _termCollection.GetTerm(shingle.Text);
            if (term != null && term.DocumentFrequency <= _stopTermLimit)
            {
                term.QueryOccurrences = (byte)shingle.Occurrences;
                queryTerms.Add(term);
            }
        }
        
        if (queryTerms.Count == 0)
            return scoreArray;

        int totalDocs = _documents.Count;
        if (totalDocs == 0)
            return scoreArray;

        // Ensure document length statistics are available
        if (_docLengths == null || _docLengths.Length != totalDocs || _avgDocLength <= 0f)
        {
            BuildInvertedLists(cancellationToken: default);
        }

        float avgdl = _avgDocLength > 0f ? _avgDocLength : 1f;
        
        // ========================================================================
        // MaxScore Algorithm (Turtle & Flood, 1995; Ding & Suel, 2011)
        // 
        // Mathematical foundation:
        // - For each term t, compute maxScore[t] = max possible BM25+ contribution
        // - Sort terms by maxScore DESCENDING (high-impact terms first)
        // - Process high-impact terms first to establish tight threshold θ
        // - For each document d with partial score S:
        //   If S + sum(maxScore of unprocessed terms) < θ, skip d
        //
        // This is EXACT (no false negatives) and requires ZERO tuning constants.
        // ========================================================================
        
        // Compute per-term max scores
        var termInfos = new TermScoreInfo[queryTerms.Count];
        
        for (int i = 0; i < queryTerms.Count; i++)
        {
            Term term = queryTerms[i];
            int df = term.DocumentFrequency;
            
            if (df <= 0)
            {
                termInfos[i] = new TermScoreInfo(term, 0f, 0f);
                continue;
            }
            
            float idf = ComputeBm25Idf(totalDocs, df);
            
            // Maximum possible BM25+ score for this term
            // Upper bound: TF=255 (max byte), dl → minimum (most favorable)
            float maxTf = 255f;
            float minDlNorm = 1f - _bm25B + _bm25B * (1f / avgdl);
            float maxBm25Core = (maxTf * (_bm25K1 + 1f)) / (maxTf + _bm25K1 * minDlNorm);
            float maxTermScore = idf * (maxBm25Core + _bm25Delta);
            
            termInfos[i] = new TermScoreInfo(term, idf, maxTermScore);
        }
        
        // Sort by maxScore DESCENDING - high-impact (rare) terms first
        // This establishes a tight threshold θ quickly
        Array.Sort(termInfos, (a, b) => b.MaxScore.CompareTo(a.MaxScore));
        
        // Compute SUFFIX sums: suffixMaxScore[i] = sum of maxScores for terms [i+1..n]
        // suffixMaxScore[i] = maximum additional score a doc can gain from remaining terms
        float[] suffixMaxScore = new float[termInfos.Length + 1];
        suffixMaxScore[termInfos.Length] = 0f;
        for (int i = termInfos.Length - 1; i >= 0; i--)
        {
            suffixMaxScore[i] = suffixMaxScore[i + 1] + termInfos[i].MaxScore;
        }
        
        float[] docScores = new float[totalDocs];
        
        // Min-heap to track top-K scores (heap[0] = smallest in top-K = threshold θ)
        var topKHeap = new PriorityQueue<int, float>(); // (docId, score) - min-heap by score
        float threshold = 0f;

        // Process terms in order of decreasing max score
        for (int i = 0; i < termInfos.Length; i++)
        {
            var info = termInfos[i];
            Term term = info.Term;
            float idf = info.Idf;
            float remainingMaxScore = suffixMaxScore[i + 1]; // Max score from terms after this one
            
            if (idf <= 0f)
                continue;

            List<int>? docIds = term.GetDocumentIds();
            List<byte>? docWeights = term.GetWeights();

            if (docIds == null || docWeights == null)
                continue;

            int postings = docIds.Count;
            for (int j = 0; j < postings; j++)
            {
                int internalId = docIds[j];
                if ((uint)internalId >= (uint)totalDocs)
                    continue;

                // MaxScore pruning: can this document possibly make top-K?
                // Upper bound = currentScore + thisTermMaxScore + remainingMaxScore
                // If upper bound < θ, skip this document
                float currentScore = docScores[internalId];
                if (topK < int.MaxValue && topKHeap.Count >= topK)
                {
                    float upperBound = currentScore + info.MaxScore + remainingMaxScore;
                    if (upperBound <= threshold)
                        continue; // Mathematically cannot enter top-K
                }

                Document? doc = _documents.GetDocument(internalId);
                if (doc == null || doc.Deleted)
                    continue;

                float tf = docWeights[j];
                if (tf <= 0f)
                    continue;

                float dl = _docLengths![internalId];
                if (dl <= 0f)
                    dl = 1f;

                // BM25+ term contribution
                float normFactor = _bm25K1 * (1f - _bm25B + _bm25B * (dl / avgdl));
                float denom = tf + normFactor;
                if (denom <= 0f)
                    continue;

                float bm25Core = (tf * (_bm25K1 + 1f)) / denom;
                float termScore = idf * (bm25Core + _bm25Delta);

                float newScore = currentScore + termScore;
                docScores[internalId] = newScore;
                
                // Update top-K heap and threshold
                if (topK < int.MaxValue)
                {
                    if (topKHeap.Count < topK)
                    {
                        topKHeap.Enqueue(internalId, newScore);
                        if (topKHeap.Count == topK)
                        {
                            // Peek at minimum in heap = threshold
                            topKHeap.TryPeek(out _, out threshold);
                        }
                    }
                    else if (newScore > threshold)
                    {
                        // New score beats threshold - update heap
                        topKHeap.EnqueueDequeue(internalId, newScore);
                        topKHeap.TryPeek(out _, out threshold);
                    }
                }

                // Track best segment if bestSegments tracking is enabled
                if (bestSegments.Height > 0 && bestSegments.Width > 0)
                {
                    int segmentNumber = doc.SegmentNumber;
                    int baseId = internalId - segmentNumber;

                    if (baseId >= 0 && baseId < bestSegments.Height &&
                        queryIndex >= 0 && queryIndex < bestSegments.Width)
                    {
                        bestSegments[baseId, queryIndex] = (byte)segmentNumber;
                    }
                }
            }
        }

        // Normalize BM25+ scores to the 0‑255 range used by the rest of the
        // engine (coverage fusion expects a byte‑scale base score).
        float maxScore = 0f;
        for (int i = 0; i < docScores.Length; i++)
        {
            if (docScores[i] > maxScore)
                maxScore = docScores[i];
        }

        if (maxScore <= 0f)
            return scoreArray;

        for (int internalId = 0; internalId < docScores.Length; internalId++)
        {
            float raw = docScores[internalId];
            if (raw <= 0f)
                continue;

            Document? doc = _documents.GetDocument(internalId);
            if (doc == null || doc.Deleted)
                continue;

            byte scaled = (byte)MathF.Min(255f, (raw / maxScore) * 255f);
            scoreArray.Add(doc.DocumentKey, scaled);
        }

        return scoreArray;
    }
    
    /// <summary>
    /// Calculates normalized and quantized query weights
    /// </summary>
    private byte[] CalculateQueryWeights(List<Term> queryTerms)
    {
        int totalDocs = _documents.Count;
        
        // Calculate raw IDF weights
        float[] rawWeights = new float[queryTerms.Count];
        float sumSquares = 0f;
        
        for (int i = 0; i < queryTerms.Count; i++)
        {
            float tf = queryTerms[i].QueryOccurrences;
            float idf = queryTerms[i].InverseDocFrequency(totalDocs, tf);
            rawWeights[i] = idf;
            sumSquares += idf * idf;
        }
        
        // Normalize (L2 norm)
        float norm = MathF.Sqrt(sumSquares);
        byte[] quantizedWeights = new byte[queryTerms.Count];
        
        for (int i = 0; i < rawWeights.Length; i++)
        {
            float normalized = norm > 0 ? rawWeights[i] / norm : 0f;
            quantizedWeights[i] = ByteAsFloat.F2B(normalized);
        }

        return quantizedWeights;
    }

    /// <summary>
    /// Standard BM25 IDF component with saturation safeguard:
    /// IDF(q) = ln( (N - df + 0.5)/(df + 0.5) + 1 )
    /// </summary>
    private static float ComputeBm25Idf(int totalDocuments, int documentFrequency)
    {
        if (documentFrequency <= 0 || totalDocuments <= 0)
            return 0f;

        float df = documentFrequency;
        float N = totalDocuments;
        float ratio = (N - df + 0.5f) / (df + 0.5f);
        if (ratio <= 0f)
            return 0f;

        return MathF.Log(ratio + 1f);
    }
    
    /// <summary>
    /// Gets the document collection
    /// </summary>
    public DocumentCollection Documents => _documents;
    
    /// <summary>
    /// Gets the term collection
    /// </summary>
    public TermCollection TermCollection => _termCollection;

    /// <summary>
    /// Saves the current index to a binary file for efficient persistence.
    /// </summary>
    public void Save(string filePath)
    {
        using FileStream stream = File.Create(filePath);
        using BinaryWriter writer = new BinaryWriter(stream);
        SaveToStream(writer);
    }

    /// <summary>
    /// Asynchronously saves the current index to a binary file.
    /// </summary>
    public async Task SaveAsync(string filePath)
    {
        await Task.Run(() => Save(filePath));
    }

    internal void SaveToStream(BinaryWriter writer)
    {
        // Header / Version
        writer.Write("INFIDEX_V1");
        
        // 1. Save Documents
        IReadOnlyList<Document> allDocs = _documents.GetAllDocuments();
        writer.Write(allDocs.Count);
        foreach (Document doc in allDocs)
        {
            writer.Write(doc.Id);
            writer.Write(doc.DocumentKey);
            writer.Write(doc.IndexedText ?? string.Empty);
            writer.Write(doc.DocumentClientInformation ?? string.Empty);
            writer.Write(doc.SegmentNumber);
            writer.Write(doc.JsonIndex);
        }
        
        // 2. Save Terms
        IEnumerable<Term> terms = _termCollection.GetAllTerms();
        writer.Write(terms.Count());
        foreach (Term term in terms)
        {
            writer.Write(term.Text ?? string.Empty);
            writer.Write(term.DocumentFrequency);
            
            List<int>? docIds = term.GetDocumentIds();
            List<byte>? weights = term.GetWeights();
            
            int count = docIds?.Count ?? 0;
            writer.Write(count);
            
            if (count > 0 && docIds != null && weights != null)
            {
                for (int i = 0; i < count; i++)
                {
                    writer.Write(docIds[i]);
                    writer.Write(weights[i]);
                }
            }
        }
    }

    /// <summary>
    /// Loads an index from a binary file.
    /// </summary>
    public static VectorModel Load(string filePath, Tokenizer tokenizer, int stopTermLimit = 1_250_000, float[]? fieldWeights = null)
    {
        VectorModel model = new VectorModel(tokenizer, stopTermLimit, fieldWeights);
        
        using (FileStream stream = File.OpenRead(filePath))
        using (BinaryReader reader = new BinaryReader(stream))
        {
            model.LoadFromStream(reader);
        }
        
        return model;
    }

    /// <summary>
    /// Asynchronously loads an index from a binary file.
    /// </summary>
    public static async Task<VectorModel> LoadAsync(string filePath, Tokenizer tokenizer, int stopTermLimit = 1_250_000, float[]? fieldWeights = null)
    {
        return await Task.Run(() => Load(filePath, tokenizer, stopTermLimit, fieldWeights));
    }
    
    internal void LoadFromStream(BinaryReader reader)
    {
        string version = reader.ReadString();
        if (version != "INFIDEX_V1")
            throw new InvalidDataException($"Unknown index format: {version}");
            
        // 1. Load Documents
        int docCount = reader.ReadInt32();
        for (int i = 0; i < docCount; i++)
        {
            int id = reader.ReadInt32();
            long key = reader.ReadInt64();
            string text = reader.ReadString();
            string info = reader.ReadString();
            int seg = reader.ReadInt32();
            int jsonIdx = reader.ReadInt32();
            
            // Create fields from loaded text (backward compatibility with old format)
            DocumentFields fields = new Api.DocumentFields();
            fields.AddField("content", text, Api.Weight.Med, indexable: true);
            
            Document doc = new Document(key, seg, fields, info) { JsonIndex = jsonIdx };
            // Set IndexedText as it's expected by tests and consumers
            doc.IndexedText = text;
            
            Document addedDoc = _documents.AddDocument(doc);
            if (addedDoc.Id != id)
            {
                // Handle potential ID mismatch if needed
            }
        }
        
        // 2. Load Terms
        int termCount = reader.ReadInt32();
        for (int i = 0; i < termCount; i++)
        {
            string text = reader.ReadString();
            int docFreq = reader.ReadInt32();
            int postingCount = reader.ReadInt32();
            
            Term term = _termCollection.CountTermUsage(text, _stopTermLimit, true);
            term.SetDocumentFrequency(docFreq);
            
            if (postingCount > 0)
            {
                for (int j = 0; j < postingCount; j++)
                {
                    int docId = reader.ReadInt32();
                    byte weight = reader.ReadByte();
                    term.AddForFastInsert(weight, docId);
                }
            }
        }
    }
}
