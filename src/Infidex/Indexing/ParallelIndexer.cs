using System.Collections.Concurrent;
using Infidex.Core;
using Infidex.Indexing.Fst;
using Infidex.Indexing.ShortQuery;
using Infidex.Tokenization;

namespace Infidex.Indexing;

/// <summary>
/// Parallel document indexer using partition-merge strategy.
/// Partitions documents across threads, builds local indexes, then merges.
/// Thread-safe and optimized for multi-core systems.
/// </summary>
internal sealed class ParallelIndexer
{
    /// <summary>
    /// Configuration for parallel indexing.
    /// </summary>
    public sealed class IndexingConfig
    {
        /// <summary>Maximum parallelism (defaults to processor count).</summary>
        public int MaxParallelism { get; set; } = Environment.ProcessorCount;
        
        /// <summary>Minimum documents per partition.</summary>
        public int MinDocumentsPerPartition { get; set; } = 100;
        
        /// <summary>Stop term limit for term frequency.</summary>
        public int StopTermLimit { get; set; } = 1_250_000;
        
        /// <summary>Token delimiters.</summary>
        public char[] Delimiters { get; set; } = [' '];
        
        /// <summary>Whether to build the short query prefix index.</summary>
        public bool BuildShortQueryIndex { get; set; } = true;
        
        /// <summary>Whether to build the FST index.</summary>
        public bool BuildFstIndex { get; set; } = true;
        
        /// <summary>Progress callback (0-100).</summary>
        public Action<int>? ProgressCallback { get; set; }
    }
    
    /// <summary>
    /// Result of a parallel indexing operation.
    /// </summary>
    public sealed class IndexingResult
    {
        public int DocumentsIndexed { get; set; }
        public int TermsCreated { get; set; }
        public int PartitionsUsed { get; set; }
        public TimeSpan Duration { get; set; }
        public bool Success { get; set; }
        public string? Error { get; set; }
    }
    
    /// <summary>
    /// Local partition index built by each thread.
    /// </summary>
    private sealed class PartitionIndex
    {
        public readonly ConcurrentDictionary<string, PartitionTerm> Terms = new ConcurrentDictionary<string, PartitionTerm>();
        public readonly List<(Document Document, string NormalizedText)> Documents = new List<(Document Document, string NormalizedText)>();
        public int DocumentStartId;
    }
    
    /// <summary>
    /// Term data accumulated within a partition.
    /// </summary>
    private sealed class PartitionTerm
    {
        public readonly string Text;
        public readonly List<(int LocalDocId, byte Weight)> Postings = new List<(int LocalDocId, byte Weight)>();
        
        public PartitionTerm(string text) => Text = text;
    }
    
    private readonly IndexingConfig _config;
    private readonly Tokenizer _tokenizer;
    private readonly TextNormalizer? _textNormalizer;
    
    public ParallelIndexer(Tokenizer tokenizer, TextNormalizer? textNormalizer = null, IndexingConfig? config = null)
    {
        _tokenizer = tokenizer;
        _textNormalizer = textNormalizer;
        _config = config ?? new IndexingConfig();
    }
    
    /// <summary>
    /// Indexes documents in parallel and returns the resulting indexes.
    /// </summary>
    public IndexingResult IndexDocuments(
        IReadOnlyList<Document> documents,
        out ConcurrentTermCollection terms,
        out ConcurrentDocumentCollection documentCollection,
        out FstIndex? fstIndex,
        out PositionalPrefixIndex? prefixIndex,
        CancellationToken cancellationToken = default)
    {
        DateTime startTime = DateTime.UtcNow;
        IndexingResult result = new IndexingResult();
        
        terms = new ConcurrentTermCollection();
        documentCollection = new ConcurrentDocumentCollection();
        fstIndex = null;
        prefixIndex = null;
        
        try
        {
            if (documents.Count == 0)
            {
                result.Success = true;
                result.Duration = DateTime.UtcNow - startTime;
                return result;
            }
            
            // Determine partitioning
            int partitionCount = CalculatePartitionCount(documents.Count);
            result.PartitionsUsed = partitionCount;
            
            // Phase 1: Build partition indexes in parallel
            PartitionIndex[] partitions = BuildPartitionIndexes(documents, partitionCount, cancellationToken);
            
            // Phase 2: Merge partitions into global collections
            MergePartitions(partitions, terms, documentCollection, cancellationToken);
            
            // Phase 3: Build auxiliary indexes
            if (_config.BuildFstIndex)
            {
                fstIndex = BuildFstIndex(terms);
            }
            
            if (_config.BuildShortQueryIndex)
            {
                prefixIndex = BuildPrefixIndex(documentCollection);
            }
            
            result.DocumentsIndexed = documentCollection.Count;
            result.TermsCreated = terms.Count;
            result.Success = true;
            result.Duration = DateTime.UtcNow - startTime;
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Error = "Indexing cancelled";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }
        
        return result;
    }
    
    /// <summary>
    /// Indexes documents into existing collections (for incremental indexing).
    /// </summary>
    public IndexingResult IndexDocumentsInto(
        IReadOnlyList<Document> documents,
        ConcurrentTermCollection terms,
        ConcurrentDocumentCollection documentCollection,
        CancellationToken cancellationToken = default)
    {
        DateTime startTime = DateTime.UtcNow;
        IndexingResult result = new IndexingResult();
        
        try
        {
            int partitionCount = CalculatePartitionCount(documents.Count);
            result.PartitionsUsed = partitionCount;
            
            PartitionIndex[] partitions = BuildPartitionIndexes(documents, partitionCount, cancellationToken);
            MergePartitions(partitions, terms, documentCollection, cancellationToken);
            
            result.DocumentsIndexed = documents.Count;
            result.TermsCreated = terms.Count;
            result.Success = true;
            result.Duration = DateTime.UtcNow - startTime;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }
        
        return result;
    }
    
    private int CalculatePartitionCount(int documentCount)
    {
        int maxPartitions = _config.MaxParallelism;
        int minDocsPerPartition = _config.MinDocumentsPerPartition;
        
        // Calculate optimal partition count
        int partitionsByDocs = (documentCount + minDocsPerPartition - 1) / minDocsPerPartition;
        return Math.Min(Math.Max(1, partitionsByDocs), maxPartitions);
    }
    
    private PartitionIndex[] BuildPartitionIndexes(
        IReadOnlyList<Document> documents,
        int partitionCount,
        CancellationToken cancellationToken)
    {
        PartitionIndex[] partitions = new PartitionIndex[partitionCount];
        for (int i = 0; i < partitionCount; i++)
            partitions[i] = new PartitionIndex();
        
        // Partition documents
        int docsPerPartition = (documents.Count + partitionCount - 1) / partitionCount;
        
        int processedDocs = 0;
        object progressLock = new object();
        
        Parallel.For(0, partitionCount, new ParallelOptions
        {
            MaxDegreeOfParallelism = _config.MaxParallelism,
            CancellationToken = cancellationToken
        }, partitionIdx =>
        {
            PartitionIndex partition = partitions[partitionIdx];
            int start = partitionIdx * docsPerPartition;
            int end = Math.Min(start + docsPerPartition, documents.Count);
            
            partition.DocumentStartId = start;
            
            for (int i = start; i < end; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                Document doc = documents[i];
                string text = doc.Fields?.GetSearchableTexts('§', out string concat) != null ? concat : doc.IndexedText ?? "";
                string normalized = text.ToLowerInvariant();
                if (_textNormalizer != null)
                    normalized = _textNormalizer.Normalize(normalized);
                
                partition.Documents.Add((doc, normalized));
                
                // Tokenize and index
                IndexDocumentInPartition(partition, i - start, normalized);
                
                // Progress reporting
                lock (progressLock)
                {
                    processedDocs++;
                    if (_config.ProgressCallback != null && processedDocs % 100 == 0)
                    {
                        int progress = (int)(processedDocs * 50.0 / documents.Count);
                        _config.ProgressCallback(progress);
                    }
                }
            }
        });
        
        return partitions;
    }
    
    private void IndexDocumentInPartition(PartitionIndex partition, int localDocId, string normalizedText)
    {
        string[] tokens = normalizedText.Split(_config.Delimiters, StringSplitOptions.RemoveEmptyEntries);
        Dictionary<string, int> termFrequencies = new Dictionary<string, int>();
        
        foreach (string token in tokens)
        {
            if (!termFrequencies.TryAdd(token, 1))
                termFrequencies[token]++;
        }
        
        foreach ((string term, int freq) in termFrequencies)
        {
            PartitionTerm partitionTerm = partition.Terms.GetOrAdd(term, t => new PartitionTerm(t));
            lock (partitionTerm.Postings)
            {
                partitionTerm.Postings.Add((localDocId, (byte)Math.Min(freq, 255)));
            }
        }
    }
    
    private void MergePartitions(
        PartitionIndex[] partitions,
        ConcurrentTermCollection globalTerms,
        ConcurrentDocumentCollection globalDocuments,
        CancellationToken cancellationToken)
    {
        // Phase 1: Add all documents (sequential to maintain ID ordering)
        Dictionary<(int PartitionIdx, int LocalId), int> idMapping = new Dictionary<(int PartitionIdx, int LocalId), int>();
        
        for (int p = 0; p < partitions.Length; p++)
        {
            foreach ((Document doc, string normalizedText) in partitions[p].Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                int localId = doc.Id;
                doc.IndexedText = normalizedText;
                Document stored = globalDocuments.AddDocument(doc);
                idMapping[(p, localId)] = stored.Id;
            }
        }
        
        _config.ProgressCallback?.Invoke(60);
        
        // Phase 2: Merge terms in parallel
        List<string> allTermTexts = partitions
            .SelectMany(p => p.Terms.Keys)
            .Distinct()
            .ToList();
        
        int processedTerms = 0;
        object progressLock = new object();
        
        Parallel.ForEach(allTermTexts, new ParallelOptions
        {
            MaxDegreeOfParallelism = _config.MaxParallelism,
            CancellationToken = cancellationToken
        }, termText =>
        {
            Term globalTerm = globalTerms.GetOrCreate(termText, _config.StopTermLimit, forFastInsert: true);
            
            // Collect all postings for this term across partitions
            List<(int GlobalDocId, byte Weight)> allPostings = new List<(int GlobalDocId, byte Weight)>();
            
            for (int p = 0; p < partitions.Length; p++)
            {
                if (partitions[p].Terms.TryGetValue(termText, out PartitionTerm? partitionTerm))
                {
                    foreach ((int localDocId, byte weight) in partitionTerm.Postings)
                    {
                        if (idMapping.TryGetValue((p, localDocId), out int globalDocId))
                        {
                            allPostings.Add((globalDocId, weight));
                        }
                    }
                }
            }
            
            // Sort by document ID for efficient merging
            allPostings.Sort((a, b) => a.GlobalDocId.CompareTo(b.GlobalDocId));
            
            // Add to global term
            lock (globalTerm)
            {
                foreach ((int globalDocId, byte weight) in allPostings)
                {
                    globalTerm.AddForFastInsert(weight, globalDocId);
                }
            }
            
            lock (progressLock)
            {
                processedTerms++;
                if (_config.ProgressCallback != null && processedTerms % 1000 == 0)
                {
                    int progress = 60 + (int)(processedTerms * 30.0 / allTermTexts.Count);
                    _config.ProgressCallback(Math.Min(90, progress));
                }
            }
        });
        
        _config.ProgressCallback?.Invoke(90);
    }
    
    private FstIndex BuildFstIndex(ConcurrentTermCollection terms)
    {
        FstBuilder builder = new FstBuilder();
        int termId = 0;
        
        foreach (Term term in terms.GetAllTerms())
        {
            if (term.Text != null)
            {
                builder.Add(term.Text, termId++);
            }
        }
        
        _config.ProgressCallback?.Invoke(95);
        return builder.Build();
    }
    
    private PositionalPrefixIndex BuildPrefixIndex(ConcurrentDocumentCollection documents)
    {
        PositionalPrefixIndex index = new PositionalPrefixIndex(delimiters: _config.Delimiters);
        
        foreach (Document doc in documents.GetAllDocuments())
        {
            if (!string.IsNullOrEmpty(doc.IndexedText))
            {
                index.IndexDocument(doc.IndexedText.ToLowerInvariant(), doc.Id);
            }
        }
        
        index.Finalize();
        _config.ProgressCallback?.Invoke(100);
        return index;
    }
}

