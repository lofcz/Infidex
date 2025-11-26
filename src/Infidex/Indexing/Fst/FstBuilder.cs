using System.Runtime.CompilerServices;

namespace Infidex.Indexing.Fst;

/// <summary>
/// Builds an FST (Finite State Transducer) from a collection of terms.
/// Supports both forward (prefix) and reverse (suffix) term insertion.
/// Thread-safe during build phase when using separate builder instances.
/// </summary>
internal sealed class FstBuilder
{
    private readonly FstBuilderNode _root = new FstBuilderNode();
    private readonly FstBuilderNode _reverseRoot = new FstBuilderNode(); // For suffix queries
    private int _termCount;
    
    /// <summary>Number of terms added to the builder.</summary>
    public int TermCount => _termCount;
    
    /// <summary>
    /// Adds a term with its associated output (posting list offset).
    /// </summary>
    /// <param name="term">The term text (will be lowercased).</param>
    /// <param name="output">The posting list offset or term ID.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(string term, int output)
    {
        if (string.IsNullOrEmpty(term))
            return;
        
        // Add to forward FST (for prefix queries)
        AddToTrie(_root, term, output);
        
        // Add reversed term to reverse FST (for suffix queries)
        AddReversed(_reverseRoot, term, output);
        
        _termCount++;
    }
    
    /// <summary>
    /// Adds a term to the forward FST only (no suffix support).
    /// Use this for terms that don't need suffix matching.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddForwardOnly(string term, int output)
    {
        if (string.IsNullOrEmpty(term))
            return;
        
        AddToTrie(_root, term, output);
        _termCount++;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddToTrie(FstBuilderNode root, string term, int output)
    {
        FstBuilderNode current = root;
        foreach (char c in term)
        {
            current = current.GetOrAddChild(c);
        }
        current.IsFinal = true;
        current.Output = output;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddReversed(FstBuilderNode root, string term, int output)
    {
        FstBuilderNode current = root;
        for (int i = term.Length - 1; i >= 0; i--)
        {
            current = current.GetOrAddChild(term[i]);
        }
        current.IsFinal = true;
        current.Output = output;
    }
    
    /// <summary>
    /// Builds and compacts the FST into flat arrays for efficient traversal.
    /// </summary>
    public FstIndex Build()
    {
        // Count nodes and arcs for pre-allocation
        int forwardNodeCount = _root.CountNodes();
        int forwardArcCount = _root.CountArcs();
        int reverseNodeCount = _reverseRoot.CountNodes();
        int reverseArcCount = _reverseRoot.CountArcs();
        
        // Allocate arrays
        FstNode[] forwardNodes = new FstNode[forwardNodeCount];
        FstArc[] forwardArcs = new FstArc[forwardArcCount];
        FstNode[] reverseNodes = new FstNode[reverseNodeCount];
        FstArc[] reverseArcs = new FstArc[reverseArcCount];
        
        // Compact forward FST
        int forwardRootIndex = CompactTrie(_root, forwardNodes, forwardArcs, out _);
        
        // Compact reverse FST
        int reverseRootIndex = CompactTrie(_reverseRoot, reverseNodes, reverseArcs, out _);
        
        return new FstIndex(
            forwardNodes, forwardArcs, forwardRootIndex,
            reverseNodes, reverseArcs, reverseRootIndex,
            _termCount);
    }
    
    /// <summary>
    /// Compacts a builder trie into flat arrays using BFS traversal.
    /// Returns the root node index.
    /// </summary>
    private static int CompactTrie(
        FstBuilderNode root,
        FstNode[] nodes,
        FstArc[] arcs,
        out int nodeCount)
    {
        Dictionary<FstBuilderNode, int> nodeToIndex = new Dictionary<FstBuilderNode, int>();
        Queue<FstBuilderNode> queue = new Queue<FstBuilderNode>();
        
        int nextNodeIndex = 0;
        int nextArcIndex = 0;
        
        // BFS to assign indices
        queue.Enqueue(root);
        nodeToIndex[root] = nextNodeIndex++;
        
        while (queue.Count > 0)
        {
            FstBuilderNode builderNode = queue.Dequeue();
            int nodeIndex = nodeToIndex[builderNode];
            
            // Create the compact node
            FstNode node = new FstNode
            {
                ArcStartIndex = nextArcIndex,
                ArcCount = (ushort)builderNode.Children.Count,
                IsFinal = builderNode.IsFinal,
                Output = builderNode.Output
            };
            
            // Sort children by label for binary search
            List<KeyValuePair<char, FstBuilderNode>> sortedChildren = builderNode.Children.OrderBy(kvp => kvp.Key).ToList();
            
            foreach ((char label, FstBuilderNode childBuilderNode) in sortedChildren)
            {
                // Assign index to child if not yet assigned
                if (!nodeToIndex.TryGetValue(childBuilderNode, out int childIndex))
                {
                    childIndex = nextNodeIndex++;
                    nodeToIndex[childBuilderNode] = childIndex;
                    queue.Enqueue(childBuilderNode);
                }
                
                // Create arc
                arcs[nextArcIndex++] = new FstArc
                {
                    Label = label,
                    TargetNodeIndex = childIndex,
                    Output = childBuilderNode.IsFinal ? childBuilderNode.Output : -1,
                    IsFinal = childBuilderNode.IsFinal
                };
            }
            
            nodes[nodeIndex] = node;
        }
        
        nodeCount = nextNodeIndex;
        return 0; // Root is always at index 0
    }
    
    /// <summary>
    /// Clears the builder for reuse.
    /// </summary>
    public void Clear()
    {
        _root.Children.Clear();
        _root.IsFinal = false;
        _root.Output = -1;
        
        _reverseRoot.Children.Clear();
        _reverseRoot.IsFinal = false;
        _reverseRoot.Output = -1;
        
        _termCount = 0;
    }
}


