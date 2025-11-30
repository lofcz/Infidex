using Infidex.Indexing.Compression;

namespace Infidex.Indexing.Segments;

internal static class BlockPostingsWriter
{
    public const int BlockSize = 128;

    public static void Write(BinaryWriter writer, List<int> docIds, List<byte> weights)
    {
        Write(writer, Zip(docIds, weights));
    }

    private static IEnumerable<(int DocId, byte Weight)> Zip(List<int> docIds, List<byte> weights)
    {
        for (int i = 0; i < docIds.Count; i++)
        {
            yield return (docIds[i], weights[i]);
        }
    }

    public static void Write(BinaryWriter writer, IEnumerable<(int DocId, byte Weight)> postings)
    {
        long startPos = writer.BaseStream.Position;
        
        // Write Header Placeholders
        writer.Write(0); // TotalCount
        writer.Write(0); // NumBlocks
        writer.Write(0L); // SkipTableOffset
        
        long firstBlockPos = writer.BaseStream.Position;
        
        int totalCount = 0;
        int numBlocks = 0;
        
        // Buffers for current block
        List<int> blockDocs = new List<int>(BlockSize);
        List<byte> blockWeights = new List<byte>(BlockSize);
        
        // Skip Table Data
        List<int> minDocs = new List<int>(); // New: Min Doc
        List<int> maxDocs = new List<int>();
        List<long> blockOffsets = new List<long>();
        List<byte> blockMaxWeights = new List<byte>();
        
        foreach (var p in postings)
        {
            blockDocs.Add(p.DocId);
            blockWeights.Add(p.Weight);
            
            if (blockDocs.Count == BlockSize)
            {
                FlushBlock(writer, blockDocs, blockWeights, minDocs, maxDocs, blockOffsets, blockMaxWeights);
                numBlocks++;
            }
            totalCount++;
        }
        
        if (blockDocs.Count > 0)
        {
            FlushBlock(writer, blockDocs, blockWeights, minDocs, maxDocs, blockOffsets, blockMaxWeights);
            numBlocks++;
        }
        
        if (totalCount == 0)
        {
             // Just fill 0 count and return
             long curr = writer.BaseStream.Position;
             writer.BaseStream.Seek(startPos, SeekOrigin.Begin);
             writer.Write(0);
             writer.BaseStream.Seek(curr, SeekOrigin.Begin);
             return;
        }
        
        // Write Skip Table at the end
        long skipTableOffset = writer.BaseStream.Position;
        for (int i = 0; i < numBlocks; i++)
        {
            writer.Write(minDocs[i]);
            writer.Write(maxDocs[i]);
            writer.Write(blockOffsets[i]);
            writer.Write(blockMaxWeights[i]);
        }
        
        long endPos = writer.BaseStream.Position;
        
        // Update Header
        writer.BaseStream.Seek(startPos, SeekOrigin.Begin);
        writer.Write(totalCount);
        writer.Write(numBlocks);
        writer.Write(skipTableOffset);
        
        writer.BaseStream.Seek(endPos, SeekOrigin.Begin);
    }
    
    private static void FlushBlock(BinaryWriter writer, List<int> docs, List<byte> weights, List<int> minDocs, List<int> maxDocs, List<long> blockOffsets, List<byte> blockMaxWeights)
    {
        blockOffsets.Add(writer.BaseStream.Position);
        minDocs.Add(docs[0]);
        maxDocs.Add(docs[docs.Count - 1]);
        
        byte maxW = 0;
        foreach(var w in weights) if(w > maxW) maxW = w;
        blockMaxWeights.Add(maxW);
        
        // GroupVarInt Encode Deltas
        int[] deltas = new int[docs.Count];
        int prev = 0;
        for (int i = 0; i < docs.Count; i++)
        {
            deltas[i] = docs[i] - prev;
            prev = docs[i];
        }
        
        // We write length first (int), then bytes.
        // GroupVarInt writes to BinaryWriter directly.
        // We need to capture length.
        
        long posBefore = writer.BaseStream.Position;
        writer.Write(0); // Placeholder for length
        long startData = writer.BaseStream.Position;
        
        GroupVarInt.Write(writer, deltas);
        
        long endData = writer.BaseStream.Position;
        int len = (int)(endData - startData);
        
        writer.BaseStream.Seek(posBefore, SeekOrigin.Begin);
        writer.Write(len);
        writer.BaseStream.Seek(endData, SeekOrigin.Begin);
        
        // Encode Weights
        writer.Write(weights.ToArray());
        
        docs.Clear();
        weights.Clear();
    }
}
