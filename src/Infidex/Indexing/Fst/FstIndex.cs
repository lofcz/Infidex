using System.Buffers;
using System.Runtime.CompilerServices;

namespace Infidex.Indexing.Fst;

/// <summary>
/// Unified FST index supporting prefix, suffix, and exact term lookups.
/// Uses flat arrays for cache efficiency and future memory-mapping support.
/// Thread-safe for concurrent read access after construction.
/// </summary>
internal sealed class FstIndex
{
    private readonly FstNode[] _forwardNodes;
    private readonly FstArc[] _forwardArcs;
    private readonly int _forwardRootIndex;
    
    private readonly FstNode[] _reverseNodes;
    private readonly FstArc[] _reverseArcs;
    private readonly int _reverseRootIndex;
    
    private readonly int _termCount;
    
    /// <summary>Number of terms in the index.</summary>
    public int TermCount => _termCount;
    
    /// <summary>Whether the index is empty.</summary>
    public bool IsEmpty => _termCount == 0;
    
    public FstIndex(
        FstNode[] forwardNodes, FstArc[] forwardArcs, int forwardRootIndex,
        FstNode[] reverseNodes, FstArc[] reverseArcs, int reverseRootIndex,
        int termCount)
    {
        _forwardNodes = forwardNodes;
        _forwardArcs = forwardArcs;
        _forwardRootIndex = forwardRootIndex;
        _reverseNodes = reverseNodes;
        _reverseArcs = reverseArcs;
        _reverseRootIndex = reverseRootIndex;
        _termCount = termCount;
    }
    
    /// <summary>
    /// Creates an empty FST index.
    /// </summary>
    public static FstIndex CreateEmpty() => new FstIndex([], [], -1, [], [], -1, 0);
    
    #region Exact Match
    
    /// <summary>
    /// Looks up an exact term and returns its output (posting list offset), or -1 if not found.
    /// Complexity: O(|term|)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetExact(ReadOnlySpan<char> term)
    {
        if (term.IsEmpty || _forwardNodes.Length == 0)
            return -1;
        
        int nodeIndex = _forwardRootIndex;
        
        foreach (char c in term)
        {
            int arcIndex = FindArc(nodeIndex, c, _forwardNodes, _forwardArcs);
            if (arcIndex < 0)
                return -1;
            
            nodeIndex = _forwardArcs[arcIndex].TargetNodeIndex;
        }
        
        ref FstNode node = ref _forwardNodes[nodeIndex];
        return node.IsFinal ? node.Output : -1;
    }
    
    /// <summary>
    /// Checks if an exact term exists in the index.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsExact(ReadOnlySpan<char> term) => GetExact(term) >= 0;
    
    #endregion
    
    #region Prefix Match
    
    /// <summary>
    /// Returns all term outputs that start with the given prefix.
    /// Complexity: O(|prefix| + k) where k is the number of matching terms.
    /// </summary>
    public void GetByPrefix(ReadOnlySpan<char> prefix, List<int> outputs)
    {
        if (prefix.IsEmpty || _forwardNodes.Length == 0)
            return;
        
        // Navigate to prefix node
        int nodeIndex = _forwardRootIndex;
        
        foreach (char c in prefix)
        {
            int arcIndex = FindArc(nodeIndex, c, _forwardNodes, _forwardArcs);
            if (arcIndex < 0)
                return; // Prefix not found
            
            nodeIndex = _forwardArcs[arcIndex].TargetNodeIndex;
        }
        
        // Collect all outputs in subtree
        CollectOutputs(nodeIndex, _forwardNodes, _forwardArcs, outputs);
    }
    
    /// <summary>
    /// Returns up to <paramref name="maxOutputs"/> term outputs that start with the given prefix.
    /// This is useful for bounding work on very dense prefixes.
    /// Complexity: O(|prefix| + min(k, maxOutputs)) where k is the number of matching terms.
    /// </summary>
    public void GetByPrefix(ReadOnlySpan<char> prefix, List<int> outputs, int maxOutputs)
    {
        if (prefix.IsEmpty || _forwardNodes.Length == 0 || maxOutputs <= 0)
            return;
        
        // Navigate to prefix node
        int nodeIndex = _forwardRootIndex;
        
        foreach (char c in prefix)
        {
            int arcIndex = FindArc(nodeIndex, c, _forwardNodes, _forwardArcs);
            if (arcIndex < 0)
                return; // Prefix not found
            
            nodeIndex = _forwardArcs[arcIndex].TargetNodeIndex;
        }
        
        // Collect bounded outputs in subtree
        CollectOutputsLimited(nodeIndex, _forwardNodes, _forwardArcs, outputs, maxOutputs);
    }
    
    /// <summary>
    /// Checks if any term starts with the given prefix.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasPrefix(ReadOnlySpan<char> prefix)
    {
        if (prefix.IsEmpty || _forwardNodes.Length == 0)
            return false;
        
        int nodeIndex = _forwardRootIndex;
        
        foreach (char c in prefix)
        {
            int arcIndex = FindArc(nodeIndex, c, _forwardNodes, _forwardArcs);
            if (arcIndex < 0)
                return false;
            
            nodeIndex = _forwardArcs[arcIndex].TargetNodeIndex;
        }
        
        return true;
    }
    
    /// <summary>
    /// Returns the number of terms that start with the given prefix.
    /// </summary>
    public int CountByPrefix(ReadOnlySpan<char> prefix)
    {
        if (prefix.IsEmpty || _forwardNodes.Length == 0)
            return 0;
        
        int nodeIndex = _forwardRootIndex;
        
        foreach (char c in prefix)
        {
            int arcIndex = FindArc(nodeIndex, c, _forwardNodes, _forwardArcs);
            if (arcIndex < 0)
                return 0;
            
            nodeIndex = _forwardArcs[arcIndex].TargetNodeIndex;
        }
        
        return CountOutputs(nodeIndex, _forwardNodes, _forwardArcs);
    }
    
    #endregion
    
    #region Suffix Match
    
    /// <summary>
    /// Returns all term outputs that end with the given suffix.
    /// Uses the reverse FST for efficient lookup.
    /// Complexity: O(|suffix| + k) where k is the number of matching terms.
    /// </summary>
    public void GetBySuffix(ReadOnlySpan<char> suffix, List<int> outputs)
    {
        if (suffix.IsEmpty || _reverseNodes.Length == 0)
            return;
        
        // Navigate reverse FST with reversed suffix (i.e., forward through suffix)
        int nodeIndex = _reverseRootIndex;
        
        // Traverse suffix from end to start (which is forward in reverse FST)
        for (int i = suffix.Length - 1; i >= 0; i--)
        {
            int arcIndex = FindArc(nodeIndex, suffix[i], _reverseNodes, _reverseArcs);
            if (arcIndex < 0)
                return;
            
            nodeIndex = _reverseArcs[arcIndex].TargetNodeIndex;
        }
        
        CollectOutputs(nodeIndex, _reverseNodes, _reverseArcs, outputs);
    }
    
    /// <summary>
    /// Returns up to <paramref name="maxOutputs"/> term outputs that end with the given suffix.
    /// Uses the reverse FST for efficient lookup.
    /// Complexity: O(|suffix| + min(k, maxOutputs)) where k is the number of matching terms.
    /// </summary>
    public void GetBySuffix(ReadOnlySpan<char> suffix, List<int> outputs, int maxOutputs)
    {
        if (suffix.IsEmpty || _reverseNodes.Length == 0 || maxOutputs <= 0)
            return;
        
        int nodeIndex = _reverseRootIndex;
        
        for (int i = suffix.Length - 1; i >= 0; i--)
        {
            int arcIndex = FindArc(nodeIndex, suffix[i], _reverseNodes, _reverseArcs);
            if (arcIndex < 0)
                return;
            
            nodeIndex = _reverseArcs[arcIndex].TargetNodeIndex;
        }
        
        CollectOutputsLimited(nodeIndex, _reverseNodes, _reverseArcs, outputs, maxOutputs);
    }
    
    /// <summary>
    /// Checks if any term ends with the given suffix.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasSuffix(ReadOnlySpan<char> suffix)
    {
        if (suffix.IsEmpty || _reverseNodes.Length == 0)
            return false;
        
        int nodeIndex = _reverseRootIndex;
        
        for (int i = suffix.Length - 1; i >= 0; i--)
        {
            int arcIndex = FindArc(nodeIndex, suffix[i], _reverseNodes, _reverseArcs);
            if (arcIndex < 0)
                return false;
            
            nodeIndex = _reverseArcs[arcIndex].TargetNodeIndex;
        }
        
        return true;
    }
    
    /// <summary>
    /// Returns the number of terms that end with the given suffix.
    /// </summary>
    public int CountBySuffix(ReadOnlySpan<char> suffix)
    {
        if (suffix.IsEmpty || _reverseNodes.Length == 0)
            return 0;
        
        int nodeIndex = _reverseRootIndex;
        
        for (int i = suffix.Length - 1; i >= 0; i--)
        {
            int arcIndex = FindArc(nodeIndex, suffix[i], _reverseNodes, _reverseArcs);
            if (arcIndex < 0)
                return 0;
            
            nodeIndex = _reverseArcs[arcIndex].TargetNodeIndex;
        }
        
        return CountOutputs(nodeIndex, _reverseNodes, _reverseArcs);
    }
    
    #endregion
    
    #region Fuzzy Match (Edit Distance)

    /// <summary>
    /// Matches all terms within edit distance 1 of the query (LD1) and adds outputs to the list.
    /// </summary>
    public void MatchWithinEditDistance1(ReadOnlySpan<char> query, List<int> outputs)
    {
        if (_forwardNodes.Length == 0) return;
        
        int m = query.Length;
        if (m == 0)
        {
            // Empty query: match terms length <= 1
            ref FstNode root = ref _forwardNodes[_forwardRootIndex];
            if (root.IsFinal && root.Output >= 0) outputs.Add(root.Output);
            int start = root.ArcStartIndex;
            for(int i=0; i<root.ArcCount; i++)
            {
                ref FstArc arc = ref _forwardArcs[start + i];
                ref FstNode target = ref _forwardNodes[arc.TargetNodeIndex];
                if (target.IsFinal && target.Output >= 0) outputs.Add(target.Output);
            }
            return;
        }

        if (m > 64) 
        {
            int exact = GetExact(query);
            if (exact >= 0) outputs.Add(exact);
            return;
        }

        // Build Pattern Masks (PM)
        Span<char> pmKeys = stackalloc char[m];
        Span<ulong> pmValues = stackalloc ulong[m];
        int pmCount = 0;
        
        for (int i = 0; i < m; i++)
        {
            char c = query[i];
            int idx = -1;
            for(int k=0; k<pmCount; k++) { if (pmKeys[k] == c) { idx = k; break; } }
            
            if (idx == -1)
            {
                pmKeys[pmCount] = c;
                pmValues[pmCount] = 1UL << i;
                pmCount++;
            }
            else
            {
                pmValues[idx] |= 1UL << i;
            }
        }
        
        ulong vp = ~0UL; // All 1s
        ulong vn = 0UL;
        int score = m;
        
        SearchBitParallel(_forwardRootIndex, vp, vn, score, 0, query, pmKeys, pmValues, pmCount, outputs);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SearchBitParallel(
        int nodeIndex, ulong vp, ulong vn, int score,
        int depth, 
        ReadOnlySpan<char> query, 
        ReadOnlySpan<char> pmKeys, ReadOnlySpan<ulong> pmValues, int pmCount,
        List<int> outputs)
    {
        ref FstNode node = ref _forwardNodes[nodeIndex];
        
        // Match check
        if (score <= 1 && node.IsFinal && node.Output >= 0)
        {
            outputs.Add(node.Output);
        }
        
        // Pruning
        if (depth >= query.Length + 1) return;

        int start = node.ArcStartIndex;
        int count = node.ArcCount;
        int m = query.Length;
        ulong maskM = 1UL << (m - 1);
        
        for (int i = 0; i < count; i++)
        {
            ref FstArc arc = ref _forwardArcs[start + i];
            char c = arc.Label;
            
            // Get PM[c]
            ulong pm = 0;
            for(int k=0; k<pmCount; k++) 
            {
                if (pmKeys[k] == c) 
                {
                    pm = pmValues[k];
                    break;
                }
            }
            
            // Myers Step
            ulong x = pm | vn;
            ulong d0 = ((vp + (x & vp)) ^ vp) | x;
            ulong hn = vp & d0;
            ulong hp = vn | ~(vp | d0);
            
            ulong newVp = (hn << 1) | ~(d0 | (hp << 1));
            ulong newVn = d0 & (hp << 1);
            
            // Update score
            int newScore = score;
            if ((hp & maskM) != 0) newScore++;
            if ((hn & maskM) != 0) newScore--;

            SearchBitParallel(arc.TargetNodeIndex, newVp, newVn, newScore, depth + 1, query, pmKeys, pmValues, pmCount, outputs);
        }
    }
    
    #endregion
    
    #region Helpers
    
    /// <summary>
    /// Binary search for an arc with the given label from a node.
    /// Returns the arc index or -1 if not found.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindArc(int nodeIndex, char label, FstNode[] nodes, FstArc[] arcs)
    {
        if (nodeIndex < 0 || nodeIndex >= nodes.Length)
            return -1;
        
        ref FstNode node = ref nodes[nodeIndex];
        int start = node.ArcStartIndex;
        int count = node.ArcCount;
        
        if (count == 0)
            return -1;
        
        // Binary search for the label
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            char midLabel = arcs[start + mid].Label;
            
            if (midLabel == label)
                return start + mid;
            if (midLabel < label)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        
        return -1;
    }
    
    /// <summary>
    /// Collects all outputs from a subtree using iterative DFS.
    /// </summary>
    private static void CollectOutputs(int startNode, FstNode[] nodes, FstArc[] arcs, List<int> outputs)
    {
        if (startNode < 0 || startNode >= nodes.Length)
            return;
        
        // Use a stack for iterative DFS
        Stack<int> stack = new Stack<int>();
        stack.Push(startNode);
        
        while (stack.Count > 0)
        {
            int nodeIndex = stack.Pop();
            ref FstNode node = ref nodes[nodeIndex];
            
            if (node.IsFinal && node.Output >= 0)
                outputs.Add(node.Output);
            
            // Push children in reverse order for correct traversal order
            for (int i = node.ArcCount - 1; i >= 0; i--)
            {
                ref FstArc arc = ref arcs[node.ArcStartIndex + i];
                stack.Push(arc.TargetNodeIndex);
            }
        }
    }
    
    /// <summary>
    /// Collects up to <paramref name="maxOutputs"/> outputs from a subtree using iterative DFS.
    /// </summary>
    private static void CollectOutputsLimited(int startNode, FstNode[] nodes, FstArc[] arcs, List<int> outputs, int maxOutputs)
    {
        if (startNode < 0 || startNode >= nodes.Length || maxOutputs <= 0)
            return;
        
        Stack<int> stack = new Stack<int>();
        stack.Push(startNode);
        
        while (stack.Count > 0 && outputs.Count < maxOutputs)
        {
            int nodeIndex = stack.Pop();
            ref FstNode node = ref nodes[nodeIndex];
            
            if (node.IsFinal && node.Output >= 0)
            {
                outputs.Add(node.Output);
                if (outputs.Count >= maxOutputs)
                    break;
            }
            
            for (int i = node.ArcCount - 1; i >= 0; i--)
            {
                ref FstArc arc = ref arcs[node.ArcStartIndex + i];
                stack.Push(arc.TargetNodeIndex);
            }
        }
    }
    
    /// <summary>
    /// Counts all outputs from a subtree.
    /// </summary>
    private static int CountOutputs(int startNode, FstNode[] nodes, FstArc[] arcs)
    {
        if (startNode < 0 || startNode >= nodes.Length)
            return 0;
        
        int count = 0;
        Stack<int> stack = new Stack<int>();
        stack.Push(startNode);
        
        while (stack.Count > 0)
        {
            int nodeIndex = stack.Pop();
            ref FstNode node = ref nodes[nodeIndex];
            
            if (node.IsFinal)
                count++;
            
            for (int i = 0; i < node.ArcCount; i++)
            {
                ref FstArc arc = ref arcs[node.ArcStartIndex + i];
                stack.Push(arc.TargetNodeIndex);
            }
        }
        
        return count;
    }
    
    #endregion
    
    #region Serialization Support
    
    /// <summary>
    /// Gets the forward FST data for serialization.
    /// </summary>
    internal (FstNode[] Nodes, FstArc[] Arcs, int RootIndex) GetForwardFst()
        => (_forwardNodes, _forwardArcs, _forwardRootIndex);
    
    /// <summary>
    /// Gets the reverse FST data for serialization.
    /// </summary>
    internal (FstNode[] Nodes, FstArc[] Arcs, int RootIndex) GetReverseFst()
        => (_reverseNodes, _reverseArcs, _reverseRootIndex);
    
    #endregion

    #region Iteration

    public IEnumerable<string> EnumerateTerms()
    {
        if (_forwardNodes.Length == 0) yield break;

        var stack = new Stack<(int NodeIndex, string Prefix)>();
        stack.Push((_forwardRootIndex, ""));

        // Use a more complex DFS to yield in order
        // Standard Stack DFS might yield in reverse if we push children in order.
        // To yield in lexicographical order, we should traverse children in order.
        // Since we can't easily pause a recursive traversal with "yield return" across stack frames without recursion,
        // we can use a recursive helper function which is simpler in C# with yield return.
        
        foreach (var term in EnumerateTermsRecursive(_forwardRootIndex, ""))
        {
            yield return term;
        }
    }

    private IEnumerable<string> EnumerateTermsRecursive(int nodeIndex, string prefix)
    {
        // Don't use ref here as it cannot be preserved across yield return
        FstNode node = _forwardNodes[nodeIndex];
        
        if (node.IsFinal)
        {
            yield return prefix;
        }

        // Forward arcs are stored sorted by label
        int startIndex = node.ArcStartIndex;
        int count = node.ArcCount;
        
        for (int i = 0; i < count; i++)
        {
            // Copy arc data before yielding
            FstArc arc = _forwardArcs[startIndex + i];
            foreach (var term in EnumerateTermsRecursive(arc.TargetNodeIndex, prefix + arc.Label))
            {
                yield return term;
            }
        }
    }

    #endregion
}
