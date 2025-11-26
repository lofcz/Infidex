using System.Collections.Concurrent;
using Infidex.Api;
using Infidex.Core;
using Infidex.Tokenization;

namespace Infidex.Indexing;

/// <summary>
/// Unified high-performance indexer for bulk document indexing.
/// Uses a partition/merge strategy with span-based n-gram tokenization
/// (via <see cref="NGramKey"/>) to build the VectorModel's core structures.
///
/// This becomes the single authoritative path for building the index for
/// <see cref="VectorModel"/> / <see cref="SearchEngine"/> and replaces
/// earlier sequential and experimental parallel indexers.
/// </summary>
internal sealed class UnifiedIndexer
{
    private readonly Tokenizer _tokenizer;
    private readonly int _stopTermLimit;

    /// <summary>
    /// Maximum degree of parallelism for indexing.
    /// </summary>
    public int MaxParallelism { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// Minimum documents per partition before we split further.
    /// </summary>
    public int MinDocumentsPerPartition { get; set; } = 1_000;

    public UnifiedIndexer(Tokenizer tokenizer, int stopTermLimit)
    {
        _tokenizer = tokenizer;
        _stopTermLimit = stopTermLimit;
    }

    private sealed class LocalTermStats
    {
        // Aggregated term frequency (already including field weights).
        public float Tf;
    }

    private sealed class PartitionData
    {
        public readonly List<Document> Documents = new();

        // Per-term → postings within this partition.
        public readonly Dictionary<NGramKey, List<(int LocalDocId, float Tf)>> Terms =
            new();
    }

    /// <summary>
    /// Builds the index into the provided <see cref="VectorModel"/> collections.
    /// </summary>
    public void BuildIndex(
        IReadOnlyList<Document> documents,
        DocumentCollection documentCollection,
        TermCollection termCollection,
        float[] fieldWeights,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (documents.Count == 0)
            return;

        int partitionCount = CalculatePartitionCount(documents.Count);
        PartitionData[] partitions = new PartitionData[partitionCount];
        for (int i = 0; i < partitionCount; i++)
            partitions[i] = new PartitionData();

        // Phase 1: partition documents and build per-partition term stats in parallel.
        int docsPerPartition = (documents.Count + partitionCount - 1) / partitionCount;

        Parallel.For(0, partitionCount, new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxParallelism,
            CancellationToken = cancellationToken
        }, partitionIdx =>
        {
            PartitionData partition = partitions[partitionIdx];
            int start = partitionIdx * docsPerPartition;
            int end = Math.Min(start + docsPerPartition, documents.Count);

            for (int i = start; i < end; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Document doc = documents[i];
                IndexSingleDocument(doc, partition, fieldWeights);
            }
        });

        progress?.Report(40);

        // Phase 2: merge partitions into global DocumentCollection and TermCollection.
        MergePartitions(partitions, documentCollection, termCollection, progress, cancellationToken);

        progress?.Report(80);
    }

    private int CalculatePartitionCount(int documentCount)
    {
        int byDocs = (documentCount + MinDocumentsPerPartition - 1) / MinDocumentsPerPartition;
        int max = Math.Max(1, MaxParallelism);
        return Math.Min(Math.Max(1, byDocs), max);
    }

    private void IndexSingleDocument(
        Document document,
        PartitionData partition,
        float[] fieldWeights)
    {
        bool isSegmentContinuation = document.SegmentNumber > 0;

        string text;
        (ushort Position, byte WeightIndex)[] fieldBoundaries;

        if (document.Fields != null)
        {
            fieldBoundaries = document.Fields.GetSearchableTexts('§', out string concatenated);
            text = concatenated;
            document.IndexedText = concatenated;
        }
        else
        {
            fieldBoundaries = Array.Empty<(ushort, byte)>();
            text = document.IndexedText ?? string.Empty;
            document.IndexedText = text;
        }

        int localId = partition.Documents.Count;
        partition.Documents.Add(document);

        // Per-document aggregation: term → tf (already including field weights).
        Dictionary<NGramKey, LocalTermStats> localTerms = new();

        _tokenizer.EnumerateNGramsForIndexing(text, isSegmentContinuation, (key, position) =>
        {
            float fieldWeight = DetermineFieldWeight(position, fieldBoundaries, fieldWeights);

            if (!localTerms.TryGetValue(key, out LocalTermStats? stats))
            {
                stats = new LocalTermStats { Tf = fieldWeight };
                localTerms[key] = stats;
            }
            else
            {
                stats.Tf += fieldWeight;
            }
        });

        foreach ((NGramKey key, LocalTermStats stats) in localTerms)
        {
            if (!partition.Terms.TryGetValue(key, out List<(int LocalDocId, float Tf)>? postings))
            {
                postings = new List<(int, float)>();
                partition.Terms[key] = postings;
            }

            postings.Add((localId, stats.Tf));
        }
    }

    private static float DetermineFieldWeight(
        int tokenPosition,
        (ushort Position, byte WeightIndex)[] fieldBoundaries,
        float[] fieldWeights)
    {
        if (fieldBoundaries.Length == 0)
            return 1.0f;

        byte weightIndex = 0;
        for (int i = 0; i < fieldBoundaries.Length; i++)
        {
            if (fieldBoundaries[i].Position <= tokenPosition)
                weightIndex = fieldBoundaries[i].WeightIndex;
            else
                break;
        }

        return weightIndex < fieldWeights.Length ? fieldWeights[weightIndex] : 1.0f;
    }

    private void MergePartitions(
        PartitionData[] partitions,
        DocumentCollection documents,
        TermCollection termCollection,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        // Phase 2a: add all documents to the global DocumentCollection.
        // Build mapping (partitionIdx, localId) -> globalId.
        Dictionary<(int PartitionIdx, int LocalId), int> idMapping = new();

        for (int p = 0; p < partitions.Length; p++)
        {
            PartitionData partition = partitions[p];
            for (int localId = 0; localId < partition.Documents.Count; localId++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Document doc = partition.Documents[localId];
                Document stored = documents.AddDocument(doc);
                idMapping[(p, localId)] = stored.Id;
            }
        }

        progress?.Report(60);

        // Phase 2b: collect all unique term keys across partitions.
        HashSet<NGramKey> allKeys = new();
        foreach (PartitionData partition in partitions)
        {
            foreach (NGramKey key in partition.Terms.Keys)
                allKeys.Add(key);
        }

        // Phase 2c: for each term, aggregate postings and push into TermCollection/Term.
        int processed = 0;
        int total = allKeys.Count;

        foreach (NGramKey key in allKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Collect postings from all partitions.
            List<(int GlobalDocId, float Tf)> postings = new();

            for (int p = 0; p < partitions.Length; p++)
            {
                if (!partitions[p].Terms.TryGetValue(key, out List<(int LocalDocId, float Tf)>? partPostings))
                    continue;

                foreach ((int localId, float tf) in partPostings)
                {
                    if (idMapping.TryGetValue((p, localId), out int globalId))
                    {
                        postings.Add((globalId, tf));
                    }
                }
            }

            // Sort postings by global document ID to match expectations of downstream code.
            postings.Sort((a, b) => a.GlobalDocId.CompareTo(b.GlobalDocId));

            // Compute document frequency and clamp TF into byte range.
            int df = postings.Count;
            if (df == 0)
                continue;

            // Enforce stop-term limit: skip indexing if above threshold.
            if (df > _stopTermLimit)
                continue;

            // Create or get term via TermCollection to keep internal structures consistent.
            string termText = key.ToText();
            Term term = termCollection.CountTermUsage(termText, _stopTermLimit, forFastInsert: true, out bool isNew);

            if (isNew)
            {
                // We are going to set document frequency explicitly.
                term.SetDocumentFrequency(df);
            }

            foreach ((int globalDocId, float tf) in postings)
            {
                byte tfByte = (byte)Math.Min(tf, byte.MaxValue);
                term.AddForFastInsert(tfByte, globalDocId);
            }

            processed++;
            if (processed % 1_000 == 0 && total > 0)
            {
                int percent = 60 + (int)(processed * 20.0 / total);
                progress?.Report(Math.Min(80, percent));
            }
        }
    }
}


