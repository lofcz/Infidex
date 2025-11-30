using System.Text;
using Infidex.Core;
using Infidex.Indexing.Compression;
using Infidex.Indexing.Fst;

namespace Infidex.Indexing.Segments;

internal class SegmentWriter
{
    private const uint SegmentMagic = 0x494E4653; // "INFS"
    private const int SegmentVersion = 1;

    public void WriteSegment(TermCollection termCollection, int docCount, int docIdOffset, string outputPath)
    {
        var sortedTerms = termCollection.GetAllTerms()
            .Where(t => t.DocumentFrequency > 0 && t.Text != null) // Skip stop terms and nulls
            .OrderBy(t => t.Text, StringComparer.Ordinal)
            .ToList();

        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        // 1. Write Header
        writer.Write(SegmentMagic);
        writer.Write(SegmentVersion);
        writer.Write(sortedTerms.Count);
        writer.Write(docCount);

        // 2. Write Postings Data and record offsets
        List<long> offsets = new List<long>(sortedTerms.Count);
        long postingsStart = stream.Position;

        foreach (var term in sortedTerms)
        {
            offsets.Add(stream.Position);
            
            var docIds = term.GetDocumentIds();
            var weights = term.GetWeights();

            if (docIds != null && weights != null)
            {
                if (docIdOffset > 0)
                {
                    // Remap to local DocIDs
                    var localDocIds = new List<int>(docIds.Count);
                    for (int i = 0; i < docIds.Count; i++)
                    {
                        localDocIds.Add(docIds[i] - docIdOffset);
                    }
                    PostingsFormat.WritePostings(writer, localDocIds, weights);
                }
                else
                {
                    PostingsFormat.WritePostings(writer, docIds, weights);
                }
            }
            else
            {
                // Should not happen due to filter above, but handle gracefully
                writer.Write(0);
            }
        }

        // 3. Build and Write FST
        long fstStart = stream.Position;
        var fstBuilder = new FstBuilder();
        for (int i = 0; i < sortedTerms.Count; i++)
        {
            fstBuilder.Add(sortedTerms[i].Text!, i); // Map term to ordinal
        }
        var fstIndex = fstBuilder.Build();
        FstSerializer.Write(writer, fstIndex);

        // 4. Write Offsets (Monotonic compression using EliasFano)
        long offsetsStart = stream.Position;
        if (offsets.Count > 0)
        {
            // Offsets are strictly increasing (or equal if empty, but write advances stream), so monotonic.
            // However, EliasFano expects strictly increasing if we want to retrieve by index efficiently or distinct values?
            // Actually EliasFano handles monotonic.
            // But wait, the EliasFano implementation provided takes `ReadOnlySpan<long>`.
            // Let's check EliasFano.cs: "Encodes a strictly increasing sequence of integers."
            // Are file offsets strictly increasing? Yes, because we write at least "count" (4 bytes) for each posting list.
            
            var ef = EliasFano.Encode(offsets.ToArray());
            ef.Write(writer);
        }

        // 5. Footer
        long footerStart = stream.Position;
        writer.Write(postingsStart);
        writer.Write(fstStart);
        writer.Write(offsetsStart);
    }
}
