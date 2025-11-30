using Infidex.Core;
using Infidex.Indexing.Compression;

namespace Infidex.Indexing.Segments;

/// <summary>
/// Handles writing and reading of postings lists using Elias-Fano and CompactArray.
/// </summary>
internal static class PostingsFormat
{
    public static void WritePostings(BinaryWriter writer, List<int> docIds, List<byte> weights)
    {
        BlockPostingsWriter.Write(writer, docIds, weights);
    }

    public static (int[] DocIds, byte[] Weights) ReadPostings(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count == 0)
        {
            return ([], []);
        }

        // Read DocIDs
        EliasFano ef = EliasFano.Read(reader);
        int[] docIds = new int[count];
        for (int i = 0; i < count; i++)
        {
            docIds[i] = (int)ef.Get(i);
        }

        // Read Weights
        CompactArray ca = CompactArray.Read(reader);
        byte[] weights = new byte[count];
        for (int i = 0; i < count; i++)
        {
            weights[i] = (byte)ca.Get(i);
        }

        return (docIds, weights);
    }
}
