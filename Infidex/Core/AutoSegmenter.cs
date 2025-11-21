namespace Infidex.Core;

/// <summary>
/// Automatically segments long documents into overlapping chunks.
/// This improves search quality for documents exceeding the max indexed text length.
/// </summary>
public class AutoSegmenter
{
    private readonly int _targetSegmentSize;
    private readonly char[] _delimiters;
    private readonly double _overlapRatio;
    
    public AutoSegmenter(double overlapRatio, int targetSegmentSize, char[] delimiters)
    {
        _overlapRatio = overlapRatio;
        _targetSegmentSize = targetSegmentSize;
        _delimiters = new char[delimiters.Length];
        Array.Copy(delimiters, _delimiters, delimiters.Length);
    }
    
    /// <summary>
    /// Checks if any document in the list requires segmentation
    /// </summary>
    public static bool SegmentsRequired(List<CoreDocument> documents, int maxLengthOfTextToBeIndexed)
    {
        for (int i = 0; i < documents.Count; i++)
        {
            if (documents[i].IndexedText.Length > maxLengthOfTextToBeIndexed)
            {
                return true;
            }
        }
        return false;
    }
    
    /// <summary>
    /// Segments all documents in a list
    /// </summary>
    public List<CoreDocument> DoSegmentListOfDocuments(List<CoreDocument> sourceDocs)
    {
        Dictionary<long, List<int>> internalKeysPrExternalKey = new Dictionary<long, List<int>>();
        List<CoreDocument> result = [];
        
        for (int i = 0; i < sourceDocs.Count; i++)
        {
            SegmentSingleDocument(result, sourceDocs[i], internalKeysPrExternalKey, out _);
        }
        
        return result;
    }
    
    /// <summary>
    /// Segments a single document into overlapping chunks
    /// </summary>
    public void SegmentSingleDocument(
        List<CoreDocument> destinationDocs, 
        CoreDocument sourceDoc, 
        Dictionary<long, List<int>> internalKeysPrExternalKey, 
        out bool wasSegmented)
    {
        int startCount = destinationDocs.Count;
        
        try
        {
            if (sourceDoc == null || string.IsNullOrEmpty(sourceDoc.IndexedText))
            {
                wasSegmented = false;
                return;
            }
            
            if (!internalKeysPrExternalKey.ContainsKey(sourceDoc.DocumentKey))
            {
                internalKeysPrExternalKey.Add(sourceDoc.DocumentKey, []);
            }
            
            ReadOnlySpan<char> source = sourceDoc.IndexedText.AsSpan();
            int length = source.Length;
            
            // Calculate number of segments needed
            double numSegments = Math.Round(
                (length + _overlapRatio * _targetSegmentSize) / 
                (_targetSegmentSize - _targetSegmentSize * _overlapRatio));
            
            // If text is short enough, don't segment
            if (numSegments < 2.0)
            {
                CoreDocument singleDoc = new CoreDocument(sourceDoc);
                internalKeysPrExternalKey[sourceDoc.DocumentKey].Add(destinationDocs.Count);
                destinationDocs.Add(singleDoc);
                wasSegmented = false;
                return;
            }
            
            // Split text by delimiters
            Span<Range> wordRanges = stackalloc Range[Math.Min(source.Length, 10000)];
            int wordCount = source.SplitAny(wordRanges, _delimiters, StringSplitOptions.RemoveEmptyEntries);
            
            if (wordCount <= 1)
            {
                CoreDocument singleDoc = new CoreDocument(sourceDoc)
                {
                    Reserved = sourceDoc.IndexedText
                };
                internalKeysPrExternalKey[sourceDoc.DocumentKey].Add(destinationDocs.Count);
                destinationDocs.Add(singleDoc);
                wasSegmented = false;
                return;
            }
            
            double targetSize = length / numSegments * (1.0 + _overlapRatio);
            
            int currentWordIndex = 0;
            int segmentNum = 0;
            
            while (currentWordIndex < wordCount)
            {
                // Find the end word for this segment
                int endWordIndex = FindSegmentEnd(
                    currentWordIndex, 
                    wordRanges, 
                    wordCount, 
                    (int)Math.Round(targetSize), 
                    segmentNum == (int)numSegments - 1);
                
                // Extract segment text
                int startPos = (currentWordIndex > 0) ? wordRanges[currentWordIndex].End.Value : wordRanges[0].Start.Value;
                int endPos = wordRanges[endWordIndex].End.Value;
                
                if (endPos == 0)
                    endPos = wordRanges[wordCount - 1].End.Value;
                
                if (endPos < startPos)
                    break;
                
                string segmentText = source.Slice(startPos, endPos - startPos).ToString();
                
                if (segmentText.Length == 0)
                    break;
                
                // Create segment document
                CoreDocument segment = new CoreDocument(
                    sourceDoc.DocumentKey, 
                    segmentNum, 
                    segmentText, 
                    sourceDoc.DocumentClientInformation, 
                    sourceDoc.JsonIndex);
                
                // First segment keeps the original text
                if (segmentNum == 0)
                {
                    segment.Reserved = sourceDoc.IndexedText;
                }
                else
                {
                    segment.DocumentClientInformation = string.Empty;
                }
                
                internalKeysPrExternalKey[sourceDoc.DocumentKey].Add(destinationDocs.Count);
                destinationDocs.Add(segment);
                
                if (endWordIndex >= wordCount)
                    break;
                
                // Calculate overlap backtrack
                int overlapChars = (int)((endPos - startPos) * _overlapRatio);
                int backtrackWordIndex = FindBacktrackPosition(endWordIndex, wordRanges, overlapChars);
                
                segmentNum++;
                currentWordIndex = backtrackWordIndex + 1;
                
                if (backtrackWordIndex <= 0)
                    currentWordIndex = 1;
            }
        }
        finally
        {
            wasSegmented = destinationDocs.Count - startCount > 1;
        }
    }
    
    private static int FindSegmentEnd(int startWordIndex, Span<Range> wordRanges, int wordCount, int targetSize, bool isLastSegment)
    {
        if (isLastSegment)
            return wordCount;
        
        int currentSize = 0;
        int previousSize = 0;
        int startPos = (startWordIndex > 0) ? wordRanges[startWordIndex].End.Value : wordRanges[0].Start.Value;
        
        int i;
        for (i = startWordIndex; i < wordCount; i++)
        {
            previousSize = currentSize;
            currentSize = wordRanges[i].End.Value - startPos;
            
            if (currentSize >= targetSize)
                break;
        }
        
        // Choose the word index closest to target size
        int overshot = currentSize - targetSize;
        int undershot = targetSize - previousSize;
        
        return undershot < overshot ? Math.Max(i - 1, 0) : Math.Min(i, wordCount);
    }
    
    private static int FindBacktrackPosition(int endWordIndex, Span<Range> wordRanges, int overlapChars)
    {
        int currentSize = 0;
        int previousSize = 0;
        
        int i;
        for (i = endWordIndex - 1; i > 0; i--)
        {
            previousSize = currentSize;
            currentSize = wordRanges[endWordIndex].End.Value - wordRanges[i].End.Value;
            
            if (currentSize >= overlapChars)
                break;
        }
        
        // Choose position closest to target overlap
        int overshot = currentSize - overlapChars;
        int undershot = overlapChars - previousSize;
        
        if (undershot < overshot)
            return i - 1;
        
        return i;
    }
}


