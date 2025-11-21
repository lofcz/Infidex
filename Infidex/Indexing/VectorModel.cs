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
                Term term = _termCollection.CountTermUsage(shingle.Text, _stopTermLimit, forFastInsert: false);
                
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
    /// Builds inverted lists with batch processing and progress reporting.
    /// Uses two-pass normalization for proper L2 normalization.
    /// </summary>
    public void BuildInvertedLists(
        int batchDelayMs = -1, 
        int batchSize = 0, 
        CancellationToken cancellationToken = default)
    {
        _documents.GetAllDocuments(); // Trigger any cleanup
        
        int totalDocs = _documents.Count;
        float[] vectorLengths = new float[totalDocs];
        
        // FIRST CYCLE: Accumulate vector lengths
        int termCount = 0;
        int totalTerms = _termCollection.Count;
        
        foreach (Term term in _termCollection.GetAllTerms())
        {
            if (++termCount % 10 == 0 && cancellationToken.IsCancellationRequested)
                return;
            
            if (batchDelayMs >= 0 && batchSize > 0 && termCount % batchSize == 0)
                Thread.Sleep(batchDelayMs);
            
            term.FirstCycleNormalizeDocumentVectorElement(totalDocs, vectorLengths);
            
            // Report progress (0-50%)
            if (termCount % 100 == 0)
                ProgressChanged?.Invoke(this, termCount * 50 / Math.Max(totalTerms, 1));
        }
        
        // Take square roots
        for (int i = 0; i < vectorLengths.Length; i++)
        {
            vectorLengths[i] = MathF.Sqrt(vectorLengths[i]);
        }
        
        // SECOND CYCLE: Normalize and quantize weights
        termCount = 0;
        foreach (Term term in _termCollection.GetAllTerms())
        {
            if (++termCount % 10 == 0 && cancellationToken.IsCancellationRequested)
                break;
            
            if (batchDelayMs >= 0 && batchSize > 0 && termCount % batchSize == 0)
                Thread.Sleep(batchDelayMs);
            
            term.SecondCycleNormalizeDocumentVectorElement(totalDocs, vectorLengths);
            
            // Report progress (50-100%)
            if (termCount % 100 == 0)
                ProgressChanged?.Invoke(this, 50 + termCount * 50 / Math.Max(totalTerms, 1));
        }
        
        ProgressChanged?.Invoke(this, 100);
    }
    
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
    /// Searches for documents matching the query using TF-IDF cosine similarity
    /// </summary>
    /// <param name="queryText">The search query</param>
    /// <param name="bestSegments">Optional 2D array to track best-scoring segments per document (default is empty)</param>
    /// <param name="queryIndex">Column index in bestSegments for multi-field search (default 0)</param>
    internal ScoreArray Search(string queryText, Span2D<byte> bestSegments = default, int queryIndex = 0)
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
        
        // Calculate query vector weights
        byte[] queryWeights = CalculateQueryWeights(queryTerms);
        
        // Calculate scores using dot product with SpanAlloc for performance
        long pointer = 0;
        try
        {
            Span<byte> documentScores = SpanAlloc.Alloc(_documents.Count, out pointer);
            
            for (int i = 0; i < queryTerms.Count; i++)
            {
                if (queryWeights[i] == 0)
                    continue;
                
                Term term = queryTerms[i];
                List<int>? docIds = term.GetDocumentIds();
                List<byte>? docWeights = term.GetWeights();
                
                if (docIds == null || docWeights == null)
                    continue;
                
                for (int j = 0; j < docIds.Count; j++)
                {
                    int internalId = docIds[j];
                    byte docWeight = docWeights[j];
                    byte queryWeight = queryWeights[i];

                    // Multiply byte weights and scale back (matching original VectorModel behavior)
                    float scoreContribution = (docWeight * queryWeight) / 255f;
                    float currentScore = (float)(int)documentScores[internalId];
                    float contribRounded = MathF.Round(scoreContribution);
                    byte newScore = (byte)MathF.Min(contribRounded + currentScore, 255f);
                    
                    if (EnableDebugLogging)
                    {
                        Document? docForDebug = _documents.GetDocument(internalId);
                        long docKeyForDebug = docForDebug?.DocumentKey ?? internalId;
                        Console.WriteLine(
                            $"[DEBUG-VM] term=\"{term.Text}\", docKey={docKeyForDebug}, internalId={internalId}, " +
                            $"docWeight={docWeight}, queryWeight={queryWeight}, " +
                            $"contribRounded={contribRounded}, prevScore={currentScore}, newScore={newScore}");
                    }

                    documentScores[internalId] = newScore;
                    
                    // Get document for this internal ID
                    Document? doc = _documents.GetDocument(internalId);
                    if (doc != null)
                    {
                        scoreArray.Add(doc.DocumentKey, newScore);
                        
                        // Track best segment if bestSegments tracking is enabled
                        if (bestSegments.Height > 0 && bestSegments.Width > 0)
                        {
                            int segmentNumber = doc.SegmentNumber;
                            int baseId = internalId - segmentNumber;
                            
                            if (baseId >= 0 && baseId < bestSegments.Height && 
                                queryIndex >= 0 && queryIndex < bestSegments.Width)
                            {
                                // Store which segment number scored for this base document
                                bestSegments[baseId, queryIndex] = (byte)segmentNumber;
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            if (pointer != 0)
                SpanAlloc.Free(pointer);
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

    private void SaveToStream(BinaryWriter writer)
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
    
    private void LoadFromStream(BinaryReader reader)
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
            var fields = new Api.DocumentFields();
            fields.AddField("content", text, Api.Weight.Med, indexable: true);
            
            Document doc = new Document(key, seg, fields, info) { JsonIndex = jsonIdx };
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
