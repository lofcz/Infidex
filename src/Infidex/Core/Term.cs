using Infidex.Indexing;
using Infidex.Indexing.Segments;
using Infidex.Internalized.Roaring;

namespace Infidex.Core;

/// <summary>
/// Represents a term in the inverted index with its document occurrences and TF-IDF weights.
/// Can be backed by in-memory lists (indexing) or a segment reader (search).
/// </summary>
public class Term
{
    // Backing field for query term frequency (used only at search time)
    private byte _queryOccurrences;

    // Backing field for document frequency / stop-term tracking.
    //  > 0 : number of documents containing this term
    //  -1  : stop term (too frequent, ignored)

    // Memory-Index Data
    private List<byte>? _weights;
    private List<int>? _documentIds;
    
    // Segment-Index Data
    private SegmentReader? _segmentReader;
    private int _docIdOffset;

    // Bitmap-Index Data (Fuzzy union)
    private RoaringBitmap? _bitmapSource;

    /// <summary>
    /// Optional term text.
    /// </summary>
    public string? Text { get; }
    
    public int DocumentFrequency { get; private set; }

    public byte QueryOccurrences
    {
        get => _queryOccurrences;
        set => _queryOccurrences = value;
    }
    
    public Term(string text)
    {
        Text = text;
        DocumentFrequency = 0;
        _queryOccurrences = 1;
        _weights = null;
        _documentIds = null;
    }
    
    internal void SetSegmentSource(SegmentReader reader, int docIdOffset, int docFreq)
    {
        _segmentReader = reader;
        _docIdOffset = docIdOffset;
        DocumentFrequency = docFreq;
        _weights = null;
        _documentIds = null;
    }

    internal void SetBitmapSource(RoaringBitmap bitmap, int docFreq)
    {
        _bitmapSource = bitmap;
        DocumentFrequency = docFreq;
        _weights = null;
        _documentIds = null;
        _segmentReader = null;
    }

    public bool FirstCycleAdd(int documentIndex, int stopTermLimit, bool removeDuplicates, float fieldWeight = 1.0f)
    {
        if (_segmentReader != null) throw new InvalidOperationException("Cannot add to segment-backed term.");

        if (_weights == null)
        {
            _weights = new List<byte>(DocumentFrequency);
            _documentIds = new List<int>(DocumentFrequency);
        }

        if (DocumentFrequency < 0) return false;

        if (_weights != null && _documentIds != null && _weights.Count < stopTermLimit)
        {
            if (_documentIds.Count == 0)
            {
                byte initialWeight = (byte)Math.Min(Math.Round(fieldWeight), byte.MaxValue);
                _weights.Add(initialWeight);
                _documentIds.Add(documentIndex);
            }
            else
            {
                int lastDocId = _documentIds[^1];

                if (lastDocId != documentIndex)
                {
                    byte initialWeight = (byte)Math.Min(Math.Round(fieldWeight), byte.MaxValue);
                    _weights.Add(initialWeight);
                    _documentIds.Add(documentIndex);
                }
                else
                {
                    if (!removeDuplicates && _weights != null)
                    {
                        int lastWeightIndex = _weights.Count - 1;
                        float newWeight = _weights[lastWeightIndex] + fieldWeight;
                        if (newWeight <= byte.MaxValue)
                        {
                            _weights[lastWeightIndex] = (byte)Math.Round(newWeight);
                            DocumentFrequency--;
                        }
                    }
                }
            }
            return true;
        }

        DocumentFrequency = -1;
        _weights?.Clear();
        _documentIds?.Clear();
        return false;
    }
    
    public void AddForFastInsert(byte weight, int documentId)
    {
        if (_segmentReader != null) throw new InvalidOperationException("Cannot add to segment-backed term.");
        _weights ??= [];
        _documentIds ??= [];

        _documentIds.Add(documentId);
        _weights.Add(weight);
    }
    
    public bool IncrementTermUsageCounter(int stopTermLimit)
    {
        if (DocumentFrequency == -1) return true;

        DocumentFrequency++;
        if (DocumentFrequency > stopTermLimit)
        {
            DocumentFrequency = -1;
            return false;
        }

        return true;
    }
    
    public void Clear()
    {
        _weights?.Clear();
        _documentIds?.Clear();
        _weights = null;
        _documentIds = null;
        _segmentReader = null;
    }
    
    public List<int>? GetDocumentIds()
    {
        if (_segmentReader != null) throw new NotSupportedException("Cannot get raw IDs from segment backed term.");
        return DocumentFrequency <= 0 ? null : _documentIds;
    }

    public List<byte>? GetWeights()
    {
        if (_segmentReader != null) throw new NotSupportedException("Cannot get raw weights from segment backed term.");
        return DocumentFrequency <= 0 ? null : _weights;
    }

    public IPostingsEnum? GetPostingsEnum()
    {
        if (DocumentFrequency <= 0) return null;
        
        if (_segmentReader != null)
        {
            return _segmentReader.GetPostingsEnum(Text!, _docIdOffset);
        }

        if (_bitmapSource != null)
        {
            return new RoaringPostingsEnum(_bitmapSource);
        }

        return _documentIds == null ? null : new ArrayPostingsEnum(_documentIds, _weights);
    }
    
    public float InverseDocFrequency(int totalDocuments, float termFrequencyInDoc)
    {
        if (DocumentFrequency <= 0) return 1f;
        return 1f + MathF.Log10(totalDocuments * termFrequencyInDoc / DocumentFrequency);
    }
    
    public override string ToString() => $"{Text ?? "<null>"} (df: {DocumentFrequency})";

    internal void SetDocumentFrequency(int df)
    {
        DocumentFrequency = df;
    }
}
