using Infidex.Utilities;

namespace Infidex.Core;

/// <summary>
/// Represents a term in the inverted index with its document occurrences and TF-IDF weights.
/// </summary>
public class Term
{
    // Backing field for query term frequency (used only at search time)
    private byte _queryOccurrences;

    // Backing field for document frequency / stop-term tracking.
    //  > 0 : number of documents containing this term
    //  -1  : stop term (too frequent, ignored)
    private int _documentFrequency;

    // Per-document data:
    //  - during indexing, each entry stores raw term frequency in that document (TF)
    //  - after two-pass normalization, entries store quantized TF-IDF weights (0-255)
    private List<byte>? _weights;
    private List<int>? _documentIds;

    /// <summary>
    /// Optional term text (not used for scoring, but kept for debugging / tooling).
    /// </summary>
    public string? Text { get; }
    
    /// <summary>
    /// Number of documents containing this term.
    /// When this value is -1 the term is treated as a stop term.
    /// </summary>
    public int DocumentFrequency => _documentFrequency;

    /// <summary>
    /// Term frequency in query (set during search).
    /// </summary>
    public byte QueryOccurrences
    {
        get => _queryOccurrences;
        set => _queryOccurrences = value;
    }
    
    public Term(string text)
    {
        Text = text;
        _documentFrequency = 0;
        _queryOccurrences = 1;
        _weights = null;
        _documentIds = null;
    }

    /// <summary>
    /// Adds an occurrence of this term during the first indexing cycle.
    /// This method builds the inverted lists incrementally as tokens are processed.
    /// Matches the reference implementation's FirstCycleAdd behavior.
    /// </summary>
    /// <param name="documentIndex">Internal document index.</param>
    /// <param name="stopTermLimit">Maximum postings allowed before marking as stop term.</param>
    /// <param name="removeDuplicates">If true, don't increment TF for duplicate tokens (used for segments).</param>
    /// <param name="fieldWeight">Field importance multiplier (1.0 for single field, 1.5/1.25/1.0 for multi-field).</param>
    /// <returns>True if the term was added, false if it became a stop term.</returns>
    public bool FirstCycleAdd(int documentIndex, int stopTermLimit, bool removeDuplicates, float fieldWeight = 1.0f)
    {
        // Lazily allocate backing lists with capacity based on document frequency
        if (_weights == null)
        {
            _weights = new List<byte>(_documentFrequency);
            _documentIds = new List<int>(_documentFrequency);
        }

        // If already marked as stop term, ignore
        if (_documentFrequency < 0)
            return false;

        // Check if we're within the stop term limit
        if (_weights != null && _documentIds != null && _weights.Count < stopTermLimit)
        {
            if (_documentIds.Count == 0)
            {
                // First occurrence of this term
                // Apply field weight to the initial term frequency
                byte initialWeight = (byte)Math.Min(Math.Round(fieldWeight), byte.MaxValue);
                _weights.Add(initialWeight);
                _documentIds.Add(documentIndex);
            }
            else
            {
                int lastDocId = _documentIds[_documentIds.Count - 1];

                if (lastDocId != documentIndex)
                {
                    // New document
                    byte initialWeight = (byte)Math.Min(Math.Round(fieldWeight), byte.MaxValue);
                    _weights.Add(initialWeight);
                    _documentIds.Add(documentIndex);
                }
                else
                {
                    // Same document - increment term frequency unless removeDuplicates is set
                    if (!removeDuplicates && _weights != null)
                    {
                        int lastWeightIndex = _weights.Count - 1;
                        // When incrementing, add the field weight
                        float newWeight = _weights[lastWeightIndex] + fieldWeight;
                        if (newWeight <= byte.MaxValue)
                        {
                            _weights[lastWeightIndex] = (byte)Math.Round(newWeight);
                            
                            // Adjust document frequency counter since we're not adding a new document
                            _documentFrequency--;
                        }
                    }
                }
            }
            return true;
        }

        // Term exceeded stop term limit - mark as stop term and clear data
        _documentFrequency = -1;
        _weights?.Clear();
        _documentIds?.Clear();
        return false;
    }
    
    /// <summary>
    /// Adds or updates the raw term frequency for a document during indexing.
    /// Legacy method - prefer using FirstCycleAdd for streaming indexing.
    /// </summary>
    /// <param name="documentId">Internal document index.</param>
    /// <param name="rawTermFrequency">
    /// Raw term frequency in this document (clamped to byte range).
    /// </param>
    public void AddOrUpdateDocumentFrequency(int documentId, int rawTermFrequency)
    {
        if (_documentFrequency < 0)
            return; // stop term

        // Lazily allocate backing lists
        _weights ??= [];
        _documentIds ??= [];

        byte tf = (byte)Math.Min(rawTermFrequency, byte.MaxValue);

        bool debugEnabled = Environment.GetEnvironmentVariable("INFIDEX_INDEX_DEBUG") == "1";
        
        if (_documentIds.Count > 0 && _documentIds[_documentIds.Count - 1] == documentId)
        {
            // Same document as last entry – just update TF, do not add a new posting
            byte oldTf = _weights[_weights.Count - 1];
            _weights[_weights.Count - 1] = tf;
            
            if (debugEnabled)
            {
                Console.WriteLine($"[TERM-DEBUG] Updated last entry: docId={documentId}, oldTf={oldTf}, newTf={tf}");
            }
        }
        else
        {
            // New document posting
            _documentIds.Add(documentId);
            _weights.Add(tf);
            
            if (debugEnabled)
            {
                Console.WriteLine($"[TERM-DEBUG] Added new entry: docId={documentId}, tf={tf}, totalPostings={_documentIds.Count}");
            }
        }
    }
    
    /// <summary>
    /// Adds a document for fast insert (used when adding a pre-normalized vector).
    /// Semantics match the reference: the provided weight is already quantized.
    /// </summary>
    public void AddForFastInsert(byte weight, int documentId)
    {
        _weights ??= [];
        _documentIds ??= [];

        _documentIds.Add(documentId);
        _weights.Add(weight);
    }
    
    /// <summary>
    /// Increments the global usage counter for this term during the counting phase.
    /// If the counter exceeds the stop-term limit the term is marked as a stop term
    /// (DocumentFrequency = -1) and will be ignored in scoring.
    /// </summary>
    public bool IncrementTermUsageCounter(int stopTermLimit)
    {
        if (_documentFrequency == -1)
            return true; // already a stop term

        _documentFrequency++;
        if (_documentFrequency > stopTermLimit)
        {
            _documentFrequency = -1;
            return false;
        }

        return true;
    }
    
    /// <summary>
    /// Clears all per-document data for this term.
    /// </summary>
    public void Clear()
    {
        _weights?.Clear();
        _documentIds?.Clear();
        _weights = null;
        _documentIds = null;
    }
    
    /// <summary>
    /// First cycle of two-pass normalization: accumulate squared TF-IDF weights
    /// into the document vector length array.
    /// </summary>
    public void FirstCycleNormalizeDocumentVectorElement(int totalDocuments, float[] vectorLengths)
    {
        if (_weights == null || _documentIds == null || _documentFrequency <= 0)
            return;

        for (int i = 0; i < _weights.Count; i++)
        {
            float tfIdf = InverseDocFrequency(totalDocuments, _weights[i]);
            vectorLengths[_documentIds[i]] += tfIdf * tfIdf;
        }
    }
    
    /// <summary>
    /// Second cycle of two-pass normalization: divide TF-IDF by the document
    /// vector length and quantize to a byte, in-place in the postings list.
    /// </summary>
    public void SecondCycleNormalizeDocumentVectorElement(int totalDocuments, float[] vectorLengths)
    {
        if (_weights == null || _documentIds == null || _documentFrequency <= 0)
            return;

        for (int i = 0; i < _weights.Count; i++)
        {
            float rawTf = _weights[i];
            float tfIdf = InverseDocFrequency(totalDocuments, rawTf);
            float norm = vectorLengths[_documentIds[i]];
            float normalized = norm > 0f ? tfIdf / norm : 0f;
            byte quantized = ByteAsFloat.F2B(normalized);
            _weights[i] = quantized;
        }
    }
    
    /// <summary>
    /// Gets the list of internal document IDs for this term.
    /// Returns null when the term is a stop term or has no postings.
    /// </summary>
    public List<int>? GetDocumentIds()
    {
        if (_documentFrequency <= 0)
            return null;

        return _documentIds;
    }
    
    /// <summary>
    /// Gets the list of quantized term weights for this term.
    /// Returns null when the term is a stop term or has no postings.
    /// </summary>
    public List<byte>? GetWeights()
    {
        if (_documentFrequency <= 0)
            return null;

        return _weights;
    }
    
    /// <summary>
    /// Calculates the TF-IDF scalar component.
    /// Formula: 1 + log10(N * tf / df) where:
    ///   N  = total number of documents
    ///   tf = term frequency in this document (or query)
    ///   df = number of documents containing the term
    /// </summary>
    public float InverseDocFrequency(int totalDocuments, float termFrequencyInDoc)
    {
        if (_documentFrequency <= 0)
            return 1f;
        
        return 1f + MathF.Log10(totalDocuments * termFrequencyInDoc / _documentFrequency);
    }
    
    public override string ToString() => $"{Text ?? "<null>"} (df: {_documentFrequency})";

    /// <summary>
    /// Sets the document frequency directly (used for loading index).
    /// </summary>
    internal void SetDocumentFrequency(int df)
    {
        _documentFrequency = df;
    }
}
