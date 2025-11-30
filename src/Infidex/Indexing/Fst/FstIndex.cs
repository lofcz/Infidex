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
    /// Returns all term outputs within edit distance 1 of the query (LD1).
    /// Uses symmetric delete approach: query deletions + FST exact lookups.
    /// </summary>
    public void GetWithinEditDistance1(ReadOnlySpan<char> query, List<int> outputs)
    {
        // Wrapper for backward compatibility (just outputs)
        List<(string Term, int Output)> matches = new List<(string Term, int Output)>();
        GetWithinEditDistance1WithTerms(query, matches);
        foreach (var match in matches)
        {
            if (!outputs.Contains(match.Output))
                outputs.Add(match.Output);
        }
    }

    /// <summary>
    /// Returns all term outputs within edit distance 1 of the query (LD1), including the matched term string.
    /// </summary>
    public void GetWithinEditDistance1WithTerms(ReadOnlySpan<char> query, List<(string Term, int Output)> matches)
    {
        if (query.IsEmpty || _forwardNodes.Length == 0)
            return;
        
        // 1. Exact match
        int exact = GetExact(query);
        if (exact >= 0)
            matches.Add((query.ToString(), exact));
        
        // 2. Deletions from query (handles insertions in target)
        Span<char> buffer = stackalloc char[query.Length - 1];
        for (int i = 0; i < query.Length; i++)
        {
            // Create deletion variant
            int writeIdx = 0;
            for (int j = 0; j < query.Length; j++)
            {
                if (j != i)
                    buffer[writeIdx++] = query[j];
            }
            
            int output = GetExact(buffer);
            if (output >= 0)
                matches.Add((buffer.ToString(), output));
        }
        
        // 3. Collect terms that are deletions of our query (handles deletions from target)
        CollectDeletionVariantsWithTerms(query, matches);
    }
    
    private void CollectDeletionVariantsWithTerms(ReadOnlySpan<char> query, List<(string Term, int Output)> matches)
    {
        // Use stack-based DFS to avoid recursion overhead
        // But here we reconstruct the string, so we need a buffer that lives long enough?
        // Actually buffer is used to probe.
        
        Span<char> buffer = stackalloc char[query.Length + 1];
        
        for (int insertPos = 0; insertPos <= query.Length; insertPos++)
        {
            for (int i = 0; i < insertPos && i < query.Length; i++)
                buffer[i] = query[i];
            
            for (int i = insertPos; i < query.Length; i++)
                buffer[i + 1] = query[i];
            
            int nodeIndex = _forwardRootIndex;
            bool valid = true;
            
            for (int i = 0; i < insertPos && valid; i++)
            {
                int arcIndex = FindArc(nodeIndex, query[i], _forwardNodes, _forwardArcs);
                if (arcIndex < 0)
                    valid = false;
                else
                    nodeIndex = _forwardArcs[arcIndex].TargetNodeIndex;
            }
            
            if (!valid)
                continue;
            
            ref FstNode node = ref _forwardNodes[nodeIndex];
            for (int a = 0; a < node.ArcCount; a++)
            {
                ref FstArc arc = ref _forwardArcs[node.ArcStartIndex + a];
                buffer[insertPos] = arc.Label;
                
                int nextNode = arc.TargetNodeIndex;
                bool matchesQuery = true;
                
                for (int i = insertPos; i < query.Length && matchesQuery; i++)
                {
                    int nextArc = FindArc(nextNode, query[i], _forwardNodes, _forwardArcs);
                    if (nextArc < 0)
                        matchesQuery = false;
                    else
                        nextNode = _forwardArcs[nextArc].TargetNodeIndex;
                }
                
                if (matchesQuery && _forwardNodes[nextNode].IsFinal)
                {
                    int output = _forwardNodes[nextNode].Output;
                    if (output >= 0)
                        matches.Add((buffer.ToString(), output));
                }
            }
        }
    }
    
    private void CollectDeletionVariants(ReadOnlySpan<char> query, List<int> outputs)
    {
        // Keep original for compat, but implementation can delegate if needed.
        // For performance, duplicating logic is better than allocating strings if caller only wants outputs.
        // But to save code, I'll assume `GetWithinEditDistance1WithTerms` is primarily used now or I update this one.
        // I updated `GetWithinEditDistance1` to wrap `GetWithinEditDistance1WithTerms`.
        // So this method is dead code unless I revert.
        // Wait, I replaced `CollectDeletionVariants` call in `GetWithinEditDistance1`.
        // So I can remove this method or rename the new one.
        // I'll keep the new one as `CollectDeletionVariantsWithTerms` and remove the old one.
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
