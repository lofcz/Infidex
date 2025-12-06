using System.Diagnostics;
using Infidex.Core;
using Infidex.Tokenization;
using Infidex.Internalized.CommunityToolkit;
using Infidex.Indexing.Fst;
using Infidex.Indexing.ShortQuery;
using Infidex.Coverage;
using Infidex.Synonyms;
using Infidex.Indexing.Segments;
using Infidex.Internalized.Roaring;

namespace Infidex.Indexing;

/// <summary>
/// TF-IDF vector space model for relevancy ranking (Stage 1).
/// Uses FST-based term index and O(1) short query resolution.
/// Thread-safe for concurrent searching.
/// </summary>
public class VectorModel : IDisposable
{
    private readonly int _stopTermLimit;
    private readonly float[] _fieldWeights;
    private readonly SynonymMap? _synonymMap;

    private float[]? _docLengths;
    private float _avgDocLength;
    
    // FST-based indexes
    private FstIndex? _fstIndex;
    private FstBuilder? _fstBuilder;
    private PositionalPrefixIndex? _shortQueryIndex;
    private DocumentMetadataCache? _documentMetadataCache;
    
    // Segments
    private readonly List<SegmentReader> _segments = [];
    private readonly List<int> _segmentDocBases = []; 
    private int _flushedDocCount = 0; 

    // Word-level IDF cache

    // Fuzzy expansion cache
    private readonly LruCache<string, Term> _fuzzyExpansionCache = new LruCache<string, Term>(1000);

    public Tokenizer Tokenizer { get; }

    public DocumentCollection Documents { get; }

    public TermCollection TermCollection { get; }

    internal FstIndex? FstIndex => _fstIndex;
    internal PositionalPrefixIndex? ShortQueryIndex => _shortQueryIndex;
    internal ShortQueryResolver? ShortQueryResolver { get; private set; }

    internal DocumentMetadataCache? DocumentMetadataCache => _documentMetadataCache;
    internal Dictionary<string, float>? WordIdfCache { get; private set; }

    public event EventHandler<int>? ProgressChanged;
    public bool EnableDebugLogging { get; set; }

    public VectorModel(Tokenizer tokenizer, int stopTermLimit = 1_250_000, float[]? fieldWeights = null, SynonymMap? synonymMap = null)
    {
        Tokenizer = tokenizer;
        _stopTermLimit = stopTermLimit;
        TermCollection = new TermCollection();
        Documents = new DocumentCollection();
        _fieldWeights = fieldWeights ?? ConfigurationParameters.DefaultFieldWeights;
        _synonymMap = synonymMap;
        _fstBuilder = new FstBuilder();
        char[] delimiters = tokenizer.TokenizerSetup?.Delimiters ?? [' '];
        _shortQueryIndex = new PositionalPrefixIndex(delimiters: delimiters);
    }

    public Document IndexDocument(Document document)
    {
        Document doc = Documents.AddDocument(document);
        bool isSegmentContinuation = doc.SegmentNumber > 0;

        (ushort Position, byte WeightIndex)[] fieldBoundaries =
            document.Fields.GetSearchableTexts('§', out string concatenatedText);

        doc.IndexedText = concatenatedText;

        string indexText = concatenatedText;
        if (Tokenizer.TextNormalizer != null)
        {
            indexText = Tokenizer.TextNormalizer.Normalize(indexText);
        }
        indexText = indexText.ToLowerInvariant();

        if (_synonymMap != null && _synonymMap.HasCanonicalMappings && Tokenizer.TokenizerSetup != null)
        {
            indexText = _synonymMap.CanonicalizeText(indexText, Tokenizer.TokenizerSetup.Delimiters);
        }

        var state = (Model: this, Doc: doc, FieldBoundaries: fieldBoundaries, IsSegmentContinuation: isSegmentContinuation);
        Tokenizer.EnumerateTokensForIndexing(indexText, isSegmentContinuation, state, static (span, pos, s) =>
        {
            float fieldWeight = s.Model.DetermineFieldWeight(pos, s.FieldBoundaries);
            Term term = s.Model.TermCollection.CountTermUsage(span, s.Model._stopTermLimit, forFastInsert: false, out bool isNewTerm);
            
            if (isNewTerm)
            {
                s.Model._fstBuilder?.AddForwardOnly(term.Text, s.Model.TermCollection.Count - 1);
            }
            
            term.FirstCycleAdd(s.Doc.Id, s.Model._stopTermLimit, removeDuplicates: s.IsSegmentContinuation, fieldWeight);
        });
        
        _shortQueryIndex?.IndexDocument(indexText, doc.Id);

        return doc;
    }

    private float DetermineFieldWeight(int tokenPosition, (ushort Position, byte WeightIndex)[] fieldBoundaries)
    {
        if (fieldBoundaries.Length == 0) return 1.0f;

        byte weightIndex = 0;
        for (int i = 0; i < fieldBoundaries.Length; i++)
        {
            if (fieldBoundaries[i].Position <= tokenPosition)
                weightIndex = fieldBoundaries[i].WeightIndex;
            else
                break;
        }

        return weightIndex < _fieldWeights.Length ? _fieldWeights[weightIndex] : 1.0f;
    }

    public void BuildInvertedLists(int batchDelayMs = -1, int batchSize = 0, CancellationToken cancellationToken = default)
    {
        int totalDocs = Documents.Count;
        _docLengths = new float[totalDocs];
        _avgDocLength = 0f;

        int totalTerms = TermCollection.Count;
        if (totalTerms == 0 || totalDocs == 0)
        {
            ProgressChanged?.Invoke(this, 100);
            return;
        }

        int workerCount = Math.Max(1, Environment.ProcessorCount);
        int chunkSize = (totalTerms + workerCount - 1) / workerCount;

        float[][] localDocLengths = new float[workerCount][];
        int processedTerms = 0;

        Parallel.For(0, workerCount, new ParallelOptions
        {
            MaxDegreeOfParallelism = workerCount,
            CancellationToken = cancellationToken
        }, workerIndex =>
        {
            int start = workerIndex * chunkSize;
            int end = Math.Min(start + chunkSize, totalTerms);
            if (start >= end)
            {
                localDocLengths[workerIndex] = new float[totalDocs];
                return;
            }

            float[] local = new float[totalDocs];

            for (int termIndex = start; termIndex < end; termIndex++)
            {
                if (batchDelayMs >= 0 && batchSize > 0 &&
                    processedTerms > 0 && processedTerms % batchSize == 0)
                {
                    Thread.Sleep(batchDelayMs);
                }

                Term? term = TermCollection.GetTermByIndex(termIndex);
                if (term == null) continue;

                List<int>? docIds = term.GetDocumentIds();
                List<byte>? weights = term.GetWeights();

                if (docIds == null || weights == null) continue;

                int postings = docIds.Count;
                for (int i = 0; i < postings; i++)
                {
                    int internalId = docIds[i];
                    if ((uint)internalId < (uint)totalDocs)
                        local[internalId] += weights[i];
                }

                int done = Interlocked.Increment(ref processedTerms);
                if (done % 100 == 0)
                {
                    int percent = done * 100 / Math.Max(totalTerms, 1);
                    ProgressChanged?.Invoke(this, percent);
                }
            }

            localDocLengths[workerIndex] = local;
        });

        for (int d = 0; d < totalDocs; d++)
        {
            float sum = 0f;
            for (int w = 0; w < workerCount; w++)
            {
                float[] local = localDocLengths[w];
                if (local != null && d < local.Length)
                    sum += local[d];
            }
            _docLengths[d] = sum;
        }

        float totalLength = 0f;
        for (int i = 0; i < _docLengths.Length; i++)
            totalLength += _docLengths[i];

        _avgDocLength = totalDocs > 0 ? totalLength / totalDocs : 0f;
        
        BuildWordIdfCache();
        ProgressChanged?.Invoke(this, 100);
    }

    public void CalculateWeights() => BuildInvertedLists();
    
    public void BuildOptimizedIndexes()
    {
        if (_fstIndex == null && TermCollection.Count > 0)
        {
            FstBuilder builder = new FstBuilder();
            for (int i = 0; i < TermCollection.Count; i++)
            {
                Term? term = TermCollection.GetTermByIndex(i);
                if (term?.Text != null)
                {
                    builder.Add(term.Text, i);
                }
            }
            _fstIndex = builder.Build();
        }
        
        if (_shortQueryIndex != null)
        {
            _shortQueryIndex.Freeze();
            char[] delimiters = Tokenizer.TokenizerSetup?.Delimiters ?? [' '];
            ShortQueryResolver = new ShortQueryResolver(_shortQueryIndex, Documents, delimiters);
        }
        
        BuildDocumentMetadataCache();
    }
    
    private void BuildDocumentMetadataCache()
    {
        _documentMetadataCache = new DocumentMetadataCache(Documents.Count);
        char[] delimiters = Tokenizer.TokenizerSetup?.Delimiters ?? [' '];
        HashSet<char> delimiterSet = new HashSet<char>(delimiters);
        
        Parallel.For(0, Documents.Count, new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        }, docId =>
        {
            Document? doc = Documents.GetDocument(docId);
            if (doc == null || doc.Deleted || string.IsNullOrEmpty(doc.IndexedText))
            {
                _documentMetadataCache.Set(docId, DocumentMetadata.Empty);
                return;
            }
            
            string text = doc.IndexedText.ToLowerInvariant();
            ReadOnlySpan<char> textSpan = text.AsSpan();

            if (Tokenizer.TextNormalizer != null)
            {
                text = Tokenizer.TextNormalizer.Normalize(text);
                textSpan = text.AsSpan();
            }

            if (_synonymMap != null && _synonymMap.HasCanonicalMappings)
            {
                text = _synonymMap.CanonicalizeText(text, delimiters);
                textSpan = text.AsSpan();
            }
            
            string firstToken = string.Empty;
            ushort tokenCount = 0;
            int i = 0;
            
            while (i < textSpan.Length && delimiterSet.Contains(textSpan[i]))
                i++;
            
            while (i < textSpan.Length && tokenCount < ushort.MaxValue)
            {
                int tokenStart = i;
                while (i < textSpan.Length && !delimiterSet.Contains(textSpan[i]))
                    i++;
                
                int tokenLength = i - tokenStart;
                
                if (tokenLength > 0)
                {
                    if (tokenCount == 0)
                    {
                        firstToken = textSpan.Slice(tokenStart, tokenLength).ToString();
                    }
                    tokenCount++;
                }
                
                while (i < textSpan.Length && delimiterSet.Contains(textSpan[i]))
                    i++;
            }
            
            _documentMetadataCache.Set(docId, new DocumentMetadata(firstToken, tokenCount));
        });
    }
    
    public void GetTermsByPrefix(string prefix, List<int> termIds)
    {
        if (_fstIndex == null) return;
        
        var span = prefix.AsSpan();
        int count = _fstIndex.CountByPrefix(span);
        if (count == 0) return;
        
        int[] buffer = System.Buffers.ArrayPool<int>.Shared.Rent(count);
        try
        {
            int written = _fstIndex.GetByPrefix(span, buffer.AsSpan(0, count));
            termIds.EnsureCapacity(termIds.Count + written);
            for(int i = 0; i < written; i++)
                termIds.Add(buffer[i]);
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(buffer);
        }
    }
    
    public bool HasTermPrefix(string prefix)
    {
        return _fstIndex?.HasPrefix(prefix.AsSpan()) ?? false;
    }
    
    internal TopKHeap Search(string queryText, Dictionary<int, byte>? bestSegmentsMap = null, int queryIndex = 0)
        => SearchWithMaxScore(queryText, int.MaxValue, bestSegmentsMap, queryIndex);

    private struct QueryTermStat
    {
        public int TermId; // -1 if not found in FST/Dict
        public string? Text; // Only for unknown terms (for fuzzy) or fallback
        public int GlobalDf;
        public byte QueryOccurrences;
        public Term? ResolvedTerm; // Cached or Virtual Term
        public bool IsFuzzyUnion;
    }

    private struct RawToken : IComparable<RawToken>
    {
        public int TermId;
        public string? Text;

        public int CompareTo(RawToken other)
        {
            return TermId != other.TermId ? TermId.CompareTo(other.TermId) : string.CompareOrdinal(Text, other.Text);
        }
    }

    private sealed class SearchTokenizationContext
    {
        public RawToken[] RentedArray = null!;
        public int RawTokenCount;
        public long LookupTicks;
        public long VisitorCount;
        public bool EnableLogging;
        public FstIndex? FstIndex;
    }

    internal TopKHeap SearchWithMaxScore(string queryText, int topK, Dictionary<int, byte>? bestSegmentsMap = null, int queryIndex = 0)
    {
        bool enableLogging = Infidex.Scoring.FusionScorer.EnableDebugLogging;
        System.Diagnostics.Stopwatch? sw = enableLogging ? System.Diagnostics.Stopwatch.StartNew() : null;
        
        RawToken[] rentedArray = System.Buffers.ArrayPool<RawToken>.Shared.Rent(128);

        SearchTokenizationContext context = new SearchTokenizationContext
        {
            RentedArray = rentedArray,
            RawTokenCount = 0,
            EnableLogging = enableLogging,
            FstIndex = _fstIndex
        };

        long normalizeTicks = 0;

        try
        {
            string processedQuery = queryText;
            if (enableLogging && Tokenizer.TextNormalizer != null)
            {
                long normStart = System.Diagnostics.Stopwatch.GetTimestamp();
                processedQuery = Tokenizer.TextNormalizer.Normalize(queryText);
                normalizeTicks = System.Diagnostics.Stopwatch.GetTimestamp() - normStart;
            }
            
            Tokenizer.EnumerateShinglesForSearch(queryText, context, static (span, ctx) =>
            {
                long startTick = ctx.EnableLogging ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

                if (ctx.RawTokenCount >= ctx.RentedArray.Length) return; 

                int termId = -1;
                if (ctx.FstIndex != null)
                {
                    termId = ctx.FstIndex.GetExact(span);
                }
                
                if (termId >= 0)
                {
                    ctx.RentedArray[ctx.RawTokenCount++] = new RawToken { TermId = termId, Text = null };
                }
                else
                {
                    ctx.RentedArray[ctx.RawTokenCount++] = new RawToken { TermId = -1, Text = span.ToString() };
                }

                if (ctx.EnableLogging)
                {
                    long endTick = System.Diagnostics.Stopwatch.GetTimestamp();
                    ctx.LookupTicks += (endTick - startTick);
                    ctx.VisitorCount++;
                }
            });

            Span<RawToken> rawTokens = rentedArray.AsSpan(0, context.RawTokenCount);
            long lookupTicks = context.LookupTicks;
            long visitorCount = context.VisitorCount;
            // int rawTokenCount = context.RawTokenCount; // Not used directly anymore, we used it for Span slice
            
            long tokenizeMs = sw?.ElapsedMilliseconds ?? 0;
            int totalDocs = Documents.Count;
            
            if (_docLengths == null || _docLengths.Length != totalDocs || _avgDocLength <= 0f)
                BuildInvertedLists(cancellationToken: CancellationToken.None);
            rawTokens.Sort();
            
            QueryTermStat[] stats = new QueryTermStat[rawTokens.Length];
            int statsCount = 0;

            long gatherStart = enableLogging ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

            if (rawTokens.Length > 0)
            {
                // Initial item
                stats[0].TermId = rawTokens[0].TermId;
                stats[0].Text = rawTokens[0].Text;
                GatherTermInfo(ref stats[0]);
                stats[0].QueryOccurrences = 1;
                statsCount = 1;

                for (int i = 1; i < rawTokens.Length; i++)
                {
                    RawToken current = rawTokens[i];
                    // Compare with previous unique stat
                    bool same;
                    if (current.TermId >= 0)
                    {
                        same = current.TermId == stats[statsCount - 1].TermId;
                    }
                    else
                    {
                        same = string.Equals(current.Text, stats[statsCount - 1].Text, StringComparison.Ordinal);
                    }

                    if (same)
                    {
                        int newOcc = stats[statsCount - 1].QueryOccurrences + 1;
                        stats[statsCount - 1].QueryOccurrences = (byte)Math.Min(newOcc, 255);
                    }
                    else
                    {
                        // New term
                        stats[statsCount].TermId = current.TermId;
                        stats[statsCount].Text = current.Text;
                        GatherTermInfo(ref stats[statsCount]);
                        stats[statsCount].QueryOccurrences = 1;
                        statsCount++;
                    }
                }
            }

            long statsMs = (sw?.ElapsedMilliseconds ?? 0) - tokenizeMs;
            
            if (enableLogging)
            {
                long gatherTicks = System.Diagnostics.Stopwatch.GetTimestamp() - gatherStart;
                double gatherMs = (double)gatherTicks / System.Diagnostics.Stopwatch.Frequency * 1000.0;
                Console.WriteLine($"[VectorModel] Stats Break: GatherInfo={gatherMs:F3}ms, TotalStats={statsMs}ms");
            }

            // Fuzzy Expansion
            long fuzzyStart = sw?.ElapsedMilliseconds ?? 0;
            for(int k=0; k<statsCount; k++)
            {
                if (stats[k].GlobalDf == 0 && stats[k].Text != null && stats[k].Text.Length >= 4)
                {
                    ExpandMissingTerm(ref stats[k]);
                }
            }
            long fuzzyMs = (sw?.ElapsedMilliseconds ?? 0) - fuzzyStart;

            List<Bm25Scorer.TermScoreInfo> activeTermInfos = [];
            List<List<Bm25Scorer.TermScoreInfo>> segmentTermInfos = new List<List<Bm25Scorer.TermScoreInfo>>(_segments.Count);
            for(int i=0; i<_segments.Count; i++) segmentTermInfos.Add([]);

            float avgdl = _avgDocLength > 0f ? _avgDocLength : 1f;

            for (int k = 0; k < statsCount; k++)
            {
                string? text = stats[k].Text;
                int df = stats[k].GlobalDf;
                byte queryOcc = stats[k].QueryOccurrences;

                if (df <= 0 || df > _stopTermLimit) continue;

                float idf = Bm25Scorer.ComputeIdf(totalDocs, df);
                
                const float maxTf = 255f;
                const float k1 = 1.2f;
                const float b = 0.75f;
                const float delta = 1.0f;
                float minDlNorm = 1f - b + b * (1f / avgdl);
                float maxBm25Core = (maxTf * (k1 + 1f)) / (maxTf + k1 * minDlNorm);
                float maxScore = idf * (maxBm25Core + delta);

                Term? activeTerm = stats[k].ResolvedTerm;
                
                if (activeTerm != null)
                {
                    activeTerm.QueryOccurrences = queryOcc;
                    activeTermInfos.Add(new Bm25Scorer.TermScoreInfo(activeTerm, idf, maxScore));
                }

                if (!stats[k].IsFuzzyUnion)
                {
                    if (text == null && activeTerm != null) text = activeTerm.Text;

                    if (text != null)
                    {
                        for (int i = 0; i < _segments.Count; i++)
                        {
                            var segReader = _segments[i];
                            int docBase = _segmentDocBases[i];
                            
                            var postingsEnum = segReader.GetPostingsEnum(text, docBase);
                            if (postingsEnum != null)
                            {
                                var segTerm = new Term(text);
                                int segDf = (int)postingsEnum.Cost();
                                segTerm.SetSegmentSource(segReader, docBase, segDf);
                                segmentTermInfos[i].Add(new Bm25Scorer.TermScoreInfo(segTerm, idf, maxScore));
                            }
                        }
                    }
                }
            }

            long prepMs = (sw?.ElapsedMilliseconds ?? 0) - fuzzyMs - statsMs - tokenizeMs;

            TopKHeap resultHeap = Bm25Scorer.Search(
                activeTermInfos.ToArray(), 
                topK, totalDocs, _docLengths!, _avgDocLength, _stopTermLimit, Documents, bestSegmentsMap, queryIndex, _shortQueryIndex, queryText);

            long memSearchMs = (sw?.ElapsedMilliseconds ?? 0) - prepMs - fuzzyMs - statsMs - tokenizeMs;

            for (int i = 0; i < _segments.Count; i++)
            {
                if (segmentTermInfos[i].Count == 0) continue;

                TopKHeap segHeap = Bm25Scorer.Search(
                    segmentTermInfos[i].ToArray(),
                    topK, totalDocs, _docLengths!, _avgDocLength, _stopTermLimit, Documents, bestSegmentsMap, queryIndex, _shortQueryIndex, queryText);
                
                foreach (var entry in segHeap.GetTopK())
                {
                    resultHeap.Add(entry);
                }
            }

            if (enableLogging && sw != null)
            {
                double lookupMs = (double)lookupTicks / System.Diagnostics.Stopwatch.Frequency * 1000.0;
                double normalizeMs = (double)normalizeTicks / System.Diagnostics.Stopwatch.Frequency * 1000.0;
                double overheadMs = tokenizeMs - lookupMs;
                Console.WriteLine($"[VectorModel] Tokenization Break: Total={tokenizeMs}ms, NormCheck={normalizeMs:F3}ms, Lookup={lookupMs:F3}ms ({visitorCount} calls), Overhead={overheadMs:F3}ms");
                Console.WriteLine($"[VectorModel] SearchWithMaxScore: total={sw.ElapsedMilliseconds}ms, tok={tokenizeMs}ms, stats={statsMs}ms, fuzzy={fuzzyMs}ms, prep={prepMs}ms, memSearch={memSearchMs}ms, segs={_segments.Count}");
            }

            return resultHeap;
        }
        finally
        {
            System.Buffers.ArrayPool<RawToken>.Shared.Return(rentedArray);
        }
    }

    private void GatherTermInfo(ref QueryTermStat stat)
    {
        stat.GlobalDf = 0;

        // Resolve Term from TermId if available
        if (stat.TermId >= 0)
        {
            stat.ResolvedTerm = TermCollection.GetTermByIndex(stat.TermId);
        }
        else if (stat.Text != null)
        {
            // Fallback for unknown terms (e.g. from segments or unindexed)
            stat.ResolvedTerm = TermCollection.GetTerm(stat.Text);
        }

        if (stat.ResolvedTerm != null)
        {
            stat.GlobalDf += stat.ResolvedTerm.DocumentFrequency;
        }
        
        string? text = stat.Text;
        if (text == null && stat.ResolvedTerm != null)
        {
            text = stat.ResolvedTerm.Text;
        }

        if (text != null)
        {
            foreach (var segment in _segments)
            {
                var postings = segment.GetPostings(text);
                if (postings != null)
                {
                    stat.GlobalDf += postings.Value.DocIds.Length;
                }
            }
        }
    }

    private void ExpandMissingTerm(ref QueryTermStat stat)
    {
        string? text = stat.Text;
        if (text == null) return; // Should not happen for missing term (TermId should be -1)

        // 1. Check cache for Virtual Term
        if (_fuzzyExpansionCache.TryGet(text, out var cachedTerm))
        {
            stat.ResolvedTerm = cachedTerm;
            stat.GlobalDf = cachedTerm.DocumentFrequency;
            stat.IsFuzzyUnion = true;
            return;
        }
        
        List<int>[] perSegmentLists = new List<int>[_segments.Count + 1]; // +1 for memory index
        
        // Memory Index
        if (_fstIndex != null)
        {
            Span<int> memMatchesBuffer = stackalloc int[1024];
            int memCount = _fstIndex.MatchWithinEditDistance1(text.AsSpan(), memMatchesBuffer);
            
            if (memCount > 0)
            {
                var memDocs = new List<int>();
                int copyCount = Math.Min(memCount, memMatchesBuffer.Length);
                Span<int> memMatches = memMatchesBuffer.Slice(0, copyCount);
                foreach(int ordinal in memMatches)
                {
                    Term? t = TermCollection.GetTermByIndex(ordinal);
                    if (t != null)
                    {
                        var docs = t.GetDocumentIds();
                        if (docs != null && docs.Count > 0)
                        {
                            memDocs.AddRange(docs);
                        }
                    }
                }
                perSegmentLists[_segments.Count] = memDocs;
            }
        }
        
        // Segments in Parallel
        Parallel.For(0, _segments.Count, i =>
        {
            var seg = _segments[i];
            int baseDocId = _segmentDocBases[i];
            if (seg.FstIndex != null)
            {
                Span<int> segMatchesBuffer = stackalloc int[1024];
                int segCount = seg.FstIndex.MatchWithinEditDistance1(text.AsSpan(), segMatchesBuffer);
                
                if (segCount > 0)
                {
                    List<int> segDocs = [];
                    int copyCount = Math.Min(segCount, segMatchesBuffer.Length);
                    Span<int> segMatches = segMatchesBuffer.Slice(0, copyCount);
                    foreach(int ordinal in segMatches)
                    {
                        var penum = seg.GetPostingsEnumByOrdinal(ordinal, baseDocId);
                        if (penum != null)
                        {
                            while(true)
                            {
                                int doc = penum.NextDoc();
                                if (doc == PostingsEnumConstants.NO_MORE_DOCS) break;
                                segDocs.Add(doc);
                            }
                        }
                    }
                    perSegmentLists[i] = segDocs;
                }
            }
        });
        
        // Merge all lists
        int totalCount = 0;
        for(int i=0; i<perSegmentLists.Length; i++)
            if (perSegmentLists[i] != null) totalCount += perSegmentLists[i].Count;
            
        List<int> allFuzzyDocs = new List<int>(totalCount);
        for(int i=0; i<perSegmentLists.Length; i++)
            if (perSegmentLists[i] != null) allFuzzyDocs.AddRange(perSegmentLists[i]);
        
        if (allFuzzyDocs.Count > 0)
        {
            // Create "Virtual Term"
            RoaringBitmap unionBitmap = RoaringBitmap.Create(allFuzzyDocs);
            
            Term fuzzyTerm = new Term(text);
            fuzzyTerm.SetBitmapSource(unionBitmap, (int)unionBitmap.Cardinality);
            
            stat.ResolvedTerm = fuzzyTerm;
            stat.GlobalDf = fuzzyTerm.DocumentFrequency;
            stat.IsFuzzyUnion = true;
            
            // Add to cache
            _fuzzyExpansionCache.Put(text, fuzzyTerm);
        }
    }
    
    private class LruCache<K, V> where K : notnull
    {
        private readonly int _capacity;
        private readonly Dictionary<K, LinkedListNode<(K Key, V Value)>> _cache;
        private readonly LinkedList<(K Key, V Value)> _list;
        private readonly object _lock = new();

        public LruCache(int capacity)
        {
            _capacity = capacity;
            _cache = new Dictionary<K, LinkedListNode<(K Key, V Value)>>(capacity);
            _list = [];
        }

        public bool TryGet(K key, out V value)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var node))
                {
                    _list.Remove(node);
                    _list.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }
                value = default!;
                return false;
            }
        }

        public void Put(K key, V value)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var node))
                {
                    _list.Remove(node);
                    _list.AddFirst(node);
                    node.Value = (key, value);
                }
                else
                {
                    if (_cache.Count >= _capacity)
                    {
                        var last = _list.Last;
                        if (last != null)
                        {
                            _cache.Remove(last.Value.Key);
                            _list.RemoveLast();
                        }
                    }
                    var newNode = new LinkedListNode<(K, V)>((key, value));
                    _list.AddFirst(newNode);
                    _cache[key] = newNode;
                }
            }
        }
    }

    public void Flush(string segmentPath)
    {
        if (TermCollection.Count == 0) return;

        var writer = new SegmentWriter();
        writer.WriteSegment(TermCollection, Documents.Count - _flushedDocCount, _flushedDocCount, segmentPath);

        var reader = new SegmentReader(segmentPath);
        _segments.Add(reader);
        _segmentDocBases.Add(_flushedDocCount);

        _flushedDocCount = Documents.Count;

        TermCollection.ClearAllData();
        _fstBuilder = new FstBuilder();
        _fstIndex = null;
    }

    public void Dispose()
    {
        foreach(var seg in _segments) seg.Dispose();
        _segments.Clear();
    }

    public void Save(string filePath)
    {
        using FileStream stream = File.Create(filePath);
        using BinaryWriter writer = new BinaryWriter(stream);
        SaveToStream(writer);
    }

    public async Task SaveAsync(string filePath) => await Task.Run(() => Save(filePath));

    internal void SaveToStream(BinaryWriter writer)
        => VectorModelPersistence.SaveToStream(writer, Documents, TermCollection, _fstIndex, _shortQueryIndex, _documentMetadataCache);

    public static VectorModel Load(string filePath, Tokenizer tokenizer, int stopTermLimit = 1_250_000, float[]? fieldWeights = null)
    {
        VectorModel model = new VectorModel(tokenizer, stopTermLimit, fieldWeights);
        using FileStream stream = File.OpenRead(filePath);
        using BinaryReader reader = new BinaryReader(stream);
        model.LoadFromStream(reader);
        return model;
    }

    public static async Task<VectorModel> LoadAsync(string filePath, Tokenizer tokenizer, int stopTermLimit = 1_250_000, float[]? fieldWeights = null)
        => await Task.Run(() => Load(filePath, tokenizer, stopTermLimit, fieldWeights));

    internal void LoadFromStream(BinaryReader reader)
    {
        VectorModelPersistence.LoadFromStream(reader, Documents, TermCollection, _stopTermLimit, 
            out _fstIndex, out _shortQueryIndex, out _documentMetadataCache);
        
        if (_shortQueryIndex != null)
        {
            char[] delimiters = Tokenizer.TokenizerSetup?.Delimiters ?? [' '];
            ShortQueryResolver = new ShortQueryResolver(_shortQueryIndex, Documents, delimiters);
        }
    }
    
    private void BuildWordIdfCache()
    {
        int totalDocs = Documents.Count;
        if (totalDocs == 0)
        {
            WordIdfCache = new Dictionary<string, float>();
            return;
        }
        
        char[] delimiters = Tokenizer.TokenizerSetup?.Delimiters ?? [' '];
        Dictionary<string, int> wordDocFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        
        for (int docId = 0; docId < totalDocs; docId++)
        {
            Document? doc = Documents.GetDocument(docId);
            if (doc == null || doc.Deleted || string.IsNullOrEmpty(doc.IndexedText))
                continue;
            
            string normalized = doc.IndexedText.ToLowerInvariant();
            if (Tokenizer.TextNormalizer != null)
            {
                normalized = Tokenizer.TextNormalizer.Normalize(normalized);
            }
            
            string[] words = normalized.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);
            HashSet<string> uniqueWords = new HashSet<string>(words, StringComparer.OrdinalIgnoreCase);
            
            foreach (string word in uniqueWords)
            {
                if (word.Length > 0)
                {
                    wordDocFreq[word] = wordDocFreq.GetValueOrDefault(word, 0) + 1;
                }
            }
        }
        
        WordIdfCache = new Dictionary<string, float>(wordDocFreq.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (word, df) in wordDocFreq)
        {
            if (df > 0 && df <= totalDocs)
            {
                WordIdfCache[word] = Bm25Scorer.ComputeIdf(totalDocs, df);
            }
        }
    }
}
