namespace Infidex.Indexing.Fst;

/// <summary>
/// Binary serialization for FST indexes.
/// Format is designed for future memory-mapping support with contiguous data blocks.
/// </summary>
internal static class FstSerializer
{
    private const uint FST_MAGIC = 0x46535432; // "FST2" in little-endian
    private const ushort FST_VERSION = 1;

    /// <summary>
    /// Serializes an FST index to a binary writer.
    /// </summary>
    public static void Write(BinaryWriter writer, FstIndex index)
    {
        // Write header
        writer.Write(FST_MAGIC);
        writer.Write(FST_VERSION);
        writer.Write(index.TermCount);

        (FstNode[] forwardNodes, FstArc[] forwardArcs, int forwardRoot) = index.GetForwardFst();
        (FstNode[] reverseNodes, FstArc[] reverseArcs, int reverseRoot) = index.GetReverseFst();

        // Write forward FST
        WriteNodeArray(writer, forwardNodes);
        WriteArcArray(writer, forwardArcs);
        writer.Write(forwardRoot);

        // Write reverse FST
        WriteNodeArray(writer, reverseNodes);
        WriteArcArray(writer, reverseArcs);
        writer.Write(reverseRoot);
    }

    /// <summary>
    /// Deserializes an FST index from a binary reader.
    /// </summary>
    public static FstIndex Read(BinaryReader reader)
    {
        // Read and verify header
        uint magic = reader.ReadUInt32();
        if (magic != FST_MAGIC)
            throw new InvalidDataException($"Invalid FST magic number: 0x{magic:X8}");

        ushort version = reader.ReadUInt16();
        if (version != FST_VERSION)
            throw new InvalidDataException($"Unsupported FST version: {version}");

        int termCount = reader.ReadInt32();

        // Read forward FST
        FstNode[] forwardNodes = ReadNodeArray(reader);
        FstArc[] forwardArcs = ReadArcArray(reader);
        int forwardRoot = reader.ReadInt32();

        // Read reverse FST
        FstNode[] reverseNodes = ReadNodeArray(reader);
        FstArc[] reverseArcs = ReadArcArray(reader);
        int reverseRoot = reader.ReadInt32();

        return new FstIndex(
            forwardNodes, forwardArcs, forwardRoot,
            reverseNodes, reverseArcs, reverseRoot,
            termCount);
    }

    private static void WriteNodeArray(BinaryWriter writer, FstNode[] nodes)
    {
        writer.Write(nodes.Length);

        foreach (ref readonly FstNode node in nodes.AsSpan())
        {
            writer.Write(node.ArcStartIndex);
            writer.Write(node.ArcCount);
            writer.Write(node.IsFinal);
            writer.Write(node.Output);
        }
    }

    private static FstNode[] ReadNodeArray(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        FstNode[] nodes = new FstNode[length];

        for (int i = 0; i < length; i++)
        {
            nodes[i] = new FstNode
            {
                ArcStartIndex = reader.ReadInt32(),
                ArcCount = reader.ReadUInt16(),
                IsFinal = reader.ReadBoolean(),
                Output = reader.ReadInt32()
            };
        }

        return nodes;
    }

    private static void WriteArcArray(BinaryWriter writer, FstArc[] arcs)
    {
        writer.Write(arcs.Length);

        foreach (ref readonly FstArc arc in arcs.AsSpan())
        {
            writer.Write((ushort)arc.Label);
            writer.Write(arc.TargetNodeIndex);
            writer.Write(arc.Output);
            writer.Write(arc.IsFinal);
        }
    }

    private static FstArc[] ReadArcArray(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        FstArc[] arcs = new FstArc[length];

        for (int i = 0; i < length; i++)
        {
            arcs[i] = new FstArc
            {
                Label = (char)reader.ReadUInt16(),
                TargetNodeIndex = reader.ReadInt32(),
                Output = reader.ReadInt32(),
                IsFinal = reader.ReadBoolean()
            };
        }

        return arcs;
    }

}

