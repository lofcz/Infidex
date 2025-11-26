using Infidex.Core;
using Infidex.Indexing.Fst;
using Infidex.Indexing.ShortQuery;

namespace Infidex.Indexing.Incremental;

/// <summary>
/// Merges delta indexes into the main index.
/// Handles document additions, deletions, and term updates efficiently.
/// </summary>
internal sealed class IndexMerger
{
    /// <summary>
    /// Configuration for merge operations.
    /// </summary>
    public sealed class MergeConfig
    {
        /// <summary>Minimum delta documents before auto-merge.</summary>
        public int AutoMergeThreshold { get; set; } = 1000;
        
        /// <summary>Whether to compact deleted documents during merge.</summary>
        public bool CompactOnMerge { get; set; } = true;
        
        /// <summary>Whether to rebuild FST during merge.</summary>
        public bool RebuildFst { get; set; } = true;
        
        /// <summary>Whether to rebuild short query index during merge.</summary>
        public bool RebuildShortQueryIndex { get; set; } = true;
    }
    
    /// <summary>
    /// Result of a merge operation.
    /// </summary>
    public sealed class MergeResult
    {
        public int DocumentsAdded { get; set; }
        public int DocumentsRemoved { get; set; }
        public int TermsAdded { get; set; }
        public int TermsUpdated { get; set; }
        public TimeSpan Duration { get; set; }
        public bool Success { get; set; }
        public string? Error { get; set; }
    }
    
    private readonly MergeConfig _config;
    
    public IndexMerger(MergeConfig? config = null)
    {
        _config = config ?? new MergeConfig();
    }
    
    /// <summary>
    /// Merges a delta index into the main document and term collections.
    /// </summary>
    public MergeResult Merge(
        DeltaIndex delta,
        DocumentCollection mainDocuments,
        TermCollection mainTerms,
        FstBuilder mainFstBuilder,
        PositionalPrefixIndex mainPrefixIndex,
        int stopTermLimit,
        char[] delimiters)
    {
        DateTime startTime = DateTime.UtcNow;
        MergeResult result = new MergeResult();
        
        try
        {
            // Step 1: Apply deletions from delta
            ApplyDeletions(delta, mainDocuments, result);
            
            // Step 2: Add new documents from delta
            AddNewDocuments(delta, mainDocuments, mainTerms, mainFstBuilder, mainPrefixIndex, 
                stopTermLimit, delimiters, result);
            
            // Step 3: Compact if configured
            if (_config.CompactOnMerge && result.DocumentsRemoved > 0)
            {
                mainDocuments.RemoveDeletedDocuments();
            }
            
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
    
    /// <summary>
    /// Merges using concurrent collections.
    /// </summary>
    public MergeResult MergeConcurrent(
        DeltaIndex delta,
        ConcurrentDocumentCollection mainDocuments,
        ConcurrentTermCollection mainTerms,
        FstBuilder mainFstBuilder,
        PositionalPrefixIndex mainPrefixIndex,
        int stopTermLimit,
        char[] delimiters)
    {
        DateTime startTime = DateTime.UtcNow;
        MergeResult result = new MergeResult();
        
        try
        {
            // Step 1: Apply deletions
            ApplyDeletionsConcurrent(delta, mainDocuments, result);
            
            // Step 2: Add new documents
            AddNewDocumentsConcurrent(delta, mainDocuments, mainTerms, mainFstBuilder,
                mainPrefixIndex, stopTermLimit, delimiters, result);
            
            // Step 3: Compact if configured
            if (_config.CompactOnMerge && result.DocumentsRemoved > 0)
            {
                mainDocuments.Compact();
            }
            
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
    
    private void ApplyDeletions(DeltaIndex delta, DocumentCollection mainDocuments, MergeResult result)
    {
        TombstoneTracker tombstones = delta.GetTombstones();
        foreach (int deletedId in tombstones.GetDeletedIds())
        {
            Document? doc = mainDocuments.GetDocument(deletedId);
            if (doc != null && !doc.Deleted)
            {
                doc.Deleted = true;
                result.DocumentsRemoved++;
            }
        }
    }
    
    private void ApplyDeletionsConcurrent(DeltaIndex delta, ConcurrentDocumentCollection mainDocuments, MergeResult result)
    {
        TombstoneTracker tombstones = delta.GetTombstones();
        foreach (int deletedId in tombstones.GetDeletedIds())
        {
            if (mainDocuments.DeleteDocument(deletedId))
                result.DocumentsRemoved++;
        }
    }
    
    private void AddNewDocuments(
        DeltaIndex delta,
        DocumentCollection mainDocuments,
        TermCollection mainTerms,
        FstBuilder mainFstBuilder,
        PositionalPrefixIndex mainPrefixIndex,
        int stopTermLimit,
        char[] delimiters,
        MergeResult result)
    {
        ConcurrentDocumentCollection deltaDocuments = delta.GetDocuments();
        ConcurrentTermCollection deltaTerms = delta.GetTerms();
        
        // Map delta doc IDs to new main doc IDs
        Dictionary<int, int> idMapping = new Dictionary<int, int>();
        
        // Add documents
        foreach (Document deltaDoc in deltaDocuments.GetAllDocuments())
        {
            Document newDoc = mainDocuments.AddDocument(deltaDoc);
            idMapping[deltaDoc.Id] = newDoc.Id;
            result.DocumentsAdded++;
            
            // Index in prefix index
            if (mainPrefixIndex != null && !string.IsNullOrEmpty(deltaDoc.IndexedText))
            {
                mainPrefixIndex.IndexDocument(deltaDoc.IndexedText.ToLowerInvariant(), newDoc.Id);
            }
        }
        
        // Merge terms
        foreach (Term deltaTerm in deltaTerms.GetAllTerms())
        {
            if (deltaTerm.Text == null)
                continue;
            
            Term mainTerm = mainTerms.CountTermUsage(deltaTerm.Text, stopTermLimit, forFastInsert: true);
            
            // Copy postings with remapped document IDs
            List<int>? docIds = deltaTerm.GetDocumentIds();
            List<byte>? weights = deltaTerm.GetWeights();
            
            if (docIds != null && weights != null)
            {
                for (int i = 0; i < docIds.Count; i++)
                {
                    if (idMapping.TryGetValue(docIds[i], out int newDocId))
                    {
                        mainTerm.AddForFastInsert(weights[i], newDocId);
                    }
                }
                result.TermsUpdated++;
            }
            
            // Add to FST if new
            if (mainFstBuilder != null)
            {
                mainFstBuilder.AddForwardOnly(deltaTerm.Text, mainTerms.Count);
            }
        }
    }
    
    private void AddNewDocumentsConcurrent(
        DeltaIndex delta,
        ConcurrentDocumentCollection mainDocuments,
        ConcurrentTermCollection mainTerms,
        FstBuilder mainFstBuilder,
        PositionalPrefixIndex mainPrefixIndex,
        int stopTermLimit,
        char[] delimiters,
        MergeResult result)
    {
        ConcurrentDocumentCollection deltaDocuments = delta.GetDocuments();
        ConcurrentTermCollection deltaTerms = delta.GetTerms();
        
        Dictionary<int, int> idMapping = new Dictionary<int, int>();
        
        // Add documents
        using (mainDocuments.AcquireWriteLock())
        {
            foreach (Document deltaDoc in deltaDocuments.GetAllDocuments())
            {
                Document newDoc = mainDocuments.AddDocument(deltaDoc);
                idMapping[deltaDoc.Id] = newDoc.Id;
                result.DocumentsAdded++;
                
                if (mainPrefixIndex != null && !string.IsNullOrEmpty(deltaDoc.IndexedText))
                {
                    mainPrefixIndex.IndexDocument(deltaDoc.IndexedText.ToLowerInvariant(), newDoc.Id);
                }
            }
        }
        
        // Merge terms (thread-safe via ConcurrentTermCollection)
        foreach (Term deltaTerm in deltaTerms.GetAllTerms())
        {
            if (deltaTerm.Text == null)
                continue;
            
            Term mainTerm = mainTerms.GetOrCreate(deltaTerm.Text, stopTermLimit, forFastInsert: true);
            
            List<int>? docIds = deltaTerm.GetDocumentIds();
            List<byte>? weights = deltaTerm.GetWeights();
            
            if (docIds != null && weights != null)
            {
                lock (mainTerm) // Lock individual term for posting updates
                {
                    for (int i = 0; i < docIds.Count; i++)
                    {
                        if (idMapping.TryGetValue(docIds[i], out int newDocId))
                        {
                            mainTerm.AddForFastInsert(weights[i], newDocId);
                        }
                    }
                }
                result.TermsUpdated++;
            }
            
            if (mainFstBuilder != null)
            {
                lock (mainFstBuilder)
                {
                    mainFstBuilder.AddForwardOnly(deltaTerm.Text, mainTerms.Count);
                }
            }
        }
    }
    
    /// <summary>
    /// Checks if a delta should be auto-merged based on configuration.
    /// </summary>
    public bool ShouldAutoMerge(DeltaIndex delta)
    {
        return delta.DocumentCount >= _config.AutoMergeThreshold;
    }
}


