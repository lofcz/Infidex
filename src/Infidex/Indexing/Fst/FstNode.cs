using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Infidex.Indexing.Fst;

/// <summary>
/// Compact FST node representation optimized for memory efficiency and cache locality.
/// Uses a flat array structure for arcs to enable future memory-mapping.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FstArc
{
    /// <summary>The character label for this arc.</summary>
    public char Label;
    
    /// <summary>Index of the target node in the node array, or -1 if none.</summary>
    public int TargetNodeIndex;
    
    /// <summary>Output value (posting list offset) if this arc leads to a final state, -1 otherwise.</summary>
    public int Output;
    
    /// <summary>Whether the target node is a final/accepting state.</summary>
    public bool IsFinal;
}

/// <summary>
/// A node in the FST. Contains a range of arcs stored in a separate flat array.
/// This structure enables efficient binary search over arcs and prepares for mmap.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FstNode
{
    /// <summary>Start index in the global arc array.</summary>
    public int ArcStartIndex;
    
    /// <summary>Number of arcs from this node.</summary>
    public ushort ArcCount;
    
    /// <summary>Whether this node is a final/accepting state.</summary>
    public bool IsFinal;
    
    /// <summary>Output value at this node (for final states).</summary>
    public int Output;
    
    public static readonly FstNode Empty = new FstNode { ArcStartIndex = -1, ArcCount = 0, IsFinal = false, Output = -1 };
}

/// <summary>
/// Builder node used during FST construction before compaction.
/// </summary>
internal sealed class FstBuilderNode
{
    public Dictionary<char, FstBuilderNode> Children = new Dictionary<char, FstBuilderNode>();
    public bool IsFinal;
    public int Output = -1; // Posting list offset
    
    /// <summary>
    /// Gets or creates a child node for the given character.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FstBuilderNode GetOrAddChild(char c)
    {
        if (!Children.TryGetValue(c, out FstBuilderNode? child))
        {
            child = new FstBuilderNode();
            Children[c] = child;
        }
        return child;
    }
    
    /// <summary>
    /// Gets the child node for the given character, or null if not found.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FstBuilderNode? GetChild(char c)
    {
        Children.TryGetValue(c, out FstBuilderNode? child);
        return child;
    }
    
    /// <summary>
    /// Counts total nodes in subtree (including this node).
    /// </summary>
    public int CountNodes()
    {
        int count = 1;
        foreach (FstBuilderNode child in Children.Values)
            count += child.CountNodes();
        return count;
    }
    
    /// <summary>
    /// Counts total arcs in subtree (including arcs from this node).
    /// </summary>
    public int CountArcs()
    {
        int count = Children.Count;
        foreach (FstBuilderNode child in Children.Values)
            count += child.CountArcs();
        return count;
    }
}


