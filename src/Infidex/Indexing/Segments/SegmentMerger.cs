using System.Text;
using Infidex.Core;
using Infidex.Indexing.Compression;
using Infidex.Indexing.Fst;

namespace Infidex.Indexing.Segments;

internal class SegmentMerger
{
    private const uint SegmentMagic = 0x494E4653; // "INFS"
    private const int SegmentVersion = 1;

    public void MergeSegments(List<SegmentReader> readers, string outputPath)
    {
        if (readers.Count == 0) return;

        // Calculate doc bases
        int[] docBases = new int[readers.Count];
        int totalDocCount = 0;
        for (int i = 0; i < readers.Count; i++)
        {
            docBases[i] = totalDocCount;
            totalDocCount += readers[i].DocCount;
        }

        // Initialize enumerators
        var enumerators = new IEnumerator<string>[readers.Count];
        var queues = new PriorityQueue<(string Term, int ReaderIndex), string>();

        for (int i = 0; i < readers.Count; i++)
        {
            enumerators[i] = readers[i].GetAllTerms().GetEnumerator();
            if (enumerators[i].MoveNext())
            {
                var term = enumerators[i].Current;
                queues.Enqueue((term, i), term);
            }
        }

        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        // 1. Write Header (Update term count later)
        writer.Write(SegmentMagic);
        writer.Write(SegmentVersion);
        long termCountPos = stream.Position;
        writer.Write(0); // Placeholder for TermCount
        writer.Write(totalDocCount);

        // 2. Merge Terms
        List<long> offsets = new List<long>();
        long postingsStart = stream.Position;
        int mergedTermCount = 0;
        var fstBuilder = new FstBuilder();

        while (queues.Count > 0)
        {
            var head = queues.Peek();
            string currentTerm = head.Term;

            // Collect all segments that have this term
            List<int> segmentIndices = new List<int>();
            while (queues.Count > 0 && queues.Peek().Term == currentTerm)
            {
                var entry = queues.Dequeue();
                segmentIndices.Add(entry.ReaderIndex);
                
                // Advance enumerator
                if (enumerators[entry.ReaderIndex].MoveNext())
                {
                    var nextTerm = enumerators[entry.ReaderIndex].Current;
                    queues.Enqueue((nextTerm, entry.ReaderIndex), nextTerm);
                }
            }

            // Merge postings for currentTerm
            offsets.Add(stream.Position);
            MergePostings(writer, currentTerm, segmentIndices, readers, docBases);
            
            // Add to FST
            fstBuilder.Add(currentTerm, mergedTermCount);
            mergedTermCount++;
        }

        // 3. Write FST
        long fstStart = stream.Position;
        var fstIndex = fstBuilder.Build();
        FstSerializer.Write(writer, fstIndex);

        // 4. Write Offsets
        long offsetsStart = stream.Position;
        if (offsets.Count > 0)
        {
            var ef = EliasFano.Encode(offsets.ToArray());
            ef.Write(writer);
        }

        // 5. Update TermCount in Header
        long footerStart = stream.Position;
        stream.Seek(termCountPos, SeekOrigin.Begin);
        writer.Write(mergedTermCount);
        stream.Seek(footerStart, SeekOrigin.Begin);

        // 6. Footer
        writer.Write(postingsStart);
        writer.Write(fstStart);
        writer.Write(offsetsStart);
    }

    private void MergePostings(BinaryWriter writer, string term, List<int> segmentIndices, List<SegmentReader> readers, int[] docBases)
    {
        // Sort by segment index to ensure monotonic DocID increase (assuming simple append merge)
        segmentIndices.Sort();

        BlockPostingsWriter.Write(writer, StreamPostings(term, segmentIndices, readers, docBases));
    }

    private IEnumerable<(int DocId, byte Weight)> StreamPostings(string term, List<int> segmentIndices, List<SegmentReader> readers, int[] docBases)
    {
        foreach (int idx in segmentIndices)
        {
            var postingsEnum = readers[idx].GetPostingsEnum(term);
            if (postingsEnum != null)
            {
                int baseDocId = docBases[idx];
                while (true)
                {
                    int docId = postingsEnum.NextDoc();
                    if (docId == PostingsEnumConstants.NO_MORE_DOCS) break;
                    
                    yield return (docId + baseDocId, (byte)postingsEnum.Freq);
                }
            }
        }
    }
}
