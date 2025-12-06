using System.Buffers;
using System.Runtime.CompilerServices;

namespace Infidex.Indexing.Fst;

/// <summary>
/// FST index supporting prefix, suffix, and exact term lookups.
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
    
    #endregion
    
    #region Prefix Match
    
    /// <summary>
    /// Returns term outputs that start with the given prefix.
    /// Fills the outputs span and returns the number of items written.
    /// Complexity: O(|prefix| + k) where k is the number of collected terms.
    /// </summary>
    public int GetByPrefix(ReadOnlySpan<char> prefix, Span<int> outputs)
    {
        if (prefix.IsEmpty || _forwardNodes.Length == 0)
            return 0;
        
        // Navigate to prefix node
        int nodeIndex = _forwardRootIndex;
        
        foreach (char c in prefix)
        {
            int arcIndex = FindArc(nodeIndex, c, _forwardNodes, _forwardArcs);
            if (arcIndex < 0)
                return 0; // Prefix not found
            
            nodeIndex = _forwardArcs[arcIndex].TargetNodeIndex;
        }
        
        // Collect outputs in subtree
        return CollectOutputs(nodeIndex, _forwardNodes, _forwardArcs, outputs);
    }
    
    /// <summary>
    /// Checks if any term starts with the given prefix.
    /// </summary>
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
    /// Returns term outputs that end with the given suffix.
    /// Fills the outputs span and returns the number of items written.
    /// Complexity: O(|suffix| + k) where k is the number of collected terms.
    /// </summary>
    public int GetBySuffix(ReadOnlySpan<char> suffix, Span<int> outputs)
    {
        if (suffix.IsEmpty || _reverseNodes.Length == 0)
            return 0;
        
        // Navigate reverse FST with reversed suffix (i.e., forward through suffix)
        int nodeIndex = _reverseRootIndex;
        
        // Traverse suffix from end to start (which is forward in reverse FST)
        for (int i = suffix.Length - 1; i >= 0; i--)
        {
            int arcIndex = FindArc(nodeIndex, suffix[i], _reverseNodes, _reverseArcs);
            if (arcIndex < 0)
                return 0;
            
            nodeIndex = _reverseArcs[arcIndex].TargetNodeIndex;
        }
        
        return CollectOutputs(nodeIndex, _reverseNodes, _reverseArcs, outputs);
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
    /// Matches all terms within edit distance 1 of the query (LD1) and returns the count of matches.
    /// </summary>
    public int MatchWithinEditDistance1(ReadOnlySpan<char> query, Span<int> outputs)
    {
        if (_forwardNodes.Length == 0) return 0;
        
        int m = query.Length;
        int count = 0;

        if (m == 0)
        {
            // Empty query: match terms length <= 1
            ref FstNode root = ref _forwardNodes[_forwardRootIndex];
            if (root.IsFinal && root.Output >= 0) 
            {
                if (count < outputs.Length) outputs[count] = root.Output;
                count++;
            }
            
            int start = root.ArcStartIndex;
            for(int i=0; i<root.ArcCount; i++)
            {
                ref FstArc arc = ref _forwardArcs[start + i];
                ref FstNode target = ref _forwardNodes[arc.TargetNodeIndex];
                if (target.IsFinal && target.Output >= 0)
                {
                    if (count < outputs.Length) outputs[count] = target.Output;
                    count++;
                }
            }
            return count;
        }

        if (m > 64) 
        {
            // For long queries, recursive search is too deep.
            // Switch to Wagner-Fischer dynamic programming on the FST.
            return MatchEditDistance1Slow(query, outputs);
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
        
        // Iterative Search
        var stackArr = ArrayPool<SearchFrame>.Shared.Rent(64);
        int stackTop = 0;
        
        try
        {
            stackArr[stackTop++] = new SearchFrame 
            { 
                NodeIndex = _forwardRootIndex, 
                Vp = ~0UL, 
                Vn = 0UL, 
                Score = m, 
                Depth = 0 
            };
            
            ulong maskM = 1UL << (m - 1);
            
            while (stackTop > 0)
            {
                var frame = stackArr[--stackTop];
                ref FstNode node = ref _forwardNodes[frame.NodeIndex];
                
                // Match check
                if (frame.Score <= 1 && node.IsFinal && node.Output >= 0)
                {
                    if (count < outputs.Length) outputs[count] = node.Output;
                    count++;
                }
                
                // Pruning
                if (frame.Depth >= m + 1) continue;

                int start = node.ArcStartIndex;
                int arcCount = node.ArcCount;
                
                // Ensure stack capacity
                if (stackTop + arcCount > stackArr.Length)
                {
                    var newArr = ArrayPool<SearchFrame>.Shared.Rent(Math.Max(stackArr.Length * 2, stackTop + arcCount));
                    Array.Copy(stackArr, newArr, stackTop);
                    ArrayPool<SearchFrame>.Shared.Return(stackArr);
                    stackArr = newArr;
                }
                
                for (int i = arcCount - 1; i >= 0; i--)
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
                    
                    ulong x = pm | frame.Vn;
                    ulong d0 = ((frame.Vp + (x & frame.Vp)) ^ frame.Vp) | x;
                    ulong hn = frame.Vp & d0;
                    ulong hp = frame.Vn | ~(frame.Vp | d0);
                    
                    ulong newVp = (hn << 1) | ~(d0 | (hp << 1));
                    ulong newVn = d0 & (hp << 1);
                    
                    int newScore = frame.Score;
                    if ((hp & maskM) != 0) newScore++;
                    if ((hn & maskM) != 0) newScore--;

                    stackArr[stackTop++] = new SearchFrame 
                    { 
                        NodeIndex = arc.TargetNodeIndex, 
                        Vp = newVp, 
                        Vn = newVn, 
                        Score = newScore, 
                        Depth = frame.Depth + 1 
                    };
                }
            }
        }
        finally
        {
            ArrayPool<SearchFrame>.Shared.Return(stackArr);
        }
        
        return count;
    }
    
    private struct SearchFrame
    {
        public int NodeIndex;
        public ulong Vp;
        public ulong Vn;
        public int Score;
        public int Depth;
    }

    private int MatchEditDistance1Slow(ReadOnlySpan<char> query, Span<int> outputs)
    {
        int count = 0;
        int m = query.Length;
        
        // Initial row: 0, 1, 2, ..., m
        int[] initialRow = ArrayPool<int>.Shared.Rent(m + 1);
        for(int i=0; i<=m; i++) initialRow[i] = i;

        var stack = new Stack<(int NodeIndex, int[] Row)>();
        stack.Push((_forwardRootIndex, initialRow));
        
        try
        {
            while (stack.Count > 0)
            {
                var (nodeIdx, parentRow) = stack.Pop();
                ref FstNode node = ref _forwardNodes[nodeIdx];
                
                // Check match at current node
                if (parentRow[m] <= 1 && node.IsFinal && node.Output >= 0)
                {
                    if (count < outputs.Length) outputs[count++] = node.Output;
                    if (count >= outputs.Length) 
                    {
                         ArrayPool<int>.Shared.Return(parentRow);
                         return count;
                    }
                }
                
                // Pruning: if min(Row) > 1, no descendants can match within distance 1
                int minDistance = parentRow[0];
                for(int i=1; i<=m; i++) 
                    if (parentRow[i] < minDistance) minDistance = parentRow[i];
                
                if (minDistance > 1)
                {
                    ArrayPool<int>.Shared.Return(parentRow);
                    continue;
                }
                
                int arcStart = node.ArcStartIndex;
                int arcCount = node.ArcCount;
                
                for(int k=0; k<arcCount; k++)
                {
                    ref FstArc arc = ref _forwardArcs[arcStart + k];
                    char c = arc.Label;
                    
                    // Calculate next row
                    int[] nextRow = ArrayPool<int>.Shared.Rent(m + 1);
                    nextRow[0] = parentRow[0] + 1;
                    
                    for(int i=1; i<=m; i++)
                    {
                        int cost = (query[i-1] == c) ? 0 : 1;
                        int d_del = nextRow[i-1] + 1;
                        int d_ins = parentRow[i] + 1;
                        int d_sub = parentRow[i-1] + cost;
                        
                        int min = d_del;
                        if (d_ins < min) min = d_ins;
                        if (d_sub < min) min = d_sub;
                        
                        nextRow[i] = min;
                    }
                    
                    stack.Push((arc.TargetNodeIndex, nextRow));
                }
                
                ArrayPool<int>.Shared.Return(parentRow);
            }
        }
        finally
        {
            while(stack.Count > 0)
            {
                var frame = stack.Pop();
                ArrayPool<int>.Shared.Return(frame.Row);
            }
        }
        
        return count;
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
        
        // Linear scan for small node counts to avoid branch misprediction penalties
        if (count <= 8)
        {
            for (int i = 0; i < count; i++)
            {
                if (arcs[start + i].Label == label)
                    return start + i;
            }
            return -1;
        }

        // Binary search for the label
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
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
    /// Collects outputs from a subtree up to the capacity of the outputs span.
    /// Returns the number of outputs collected.
    /// </summary>
    private static int CollectOutputs(int startNode, FstNode[] nodes, FstArc[] arcs, Span<int> outputs)
    {
        if (startNode < 0 || startNode >= nodes.Length || outputs.IsEmpty)
            return 0;
        
        int count = 0;
        // Use a pool for stack to avoid allocations
        var stackArr = ArrayPool<int>.Shared.Rent(64);
        int stackTop = 0;
        
        try
        {
            stackArr[stackTop++] = startNode;
            
            while (stackTop > 0)
            {
                int nodeIndex = stackArr[--stackTop];
                ref FstNode node = ref nodes[nodeIndex];
                
                if (node.IsFinal && node.Output >= 0)
                {
                    outputs[count++] = node.Output;
                    if (count >= outputs.Length)
                        return count;
                }
                
                int arcCount = node.ArcCount;
                int start = node.ArcStartIndex;
                
                // Ensure stack capacity
                if (stackTop + arcCount > stackArr.Length)
                {
                    var newArr = ArrayPool<int>.Shared.Rent(Math.Max(stackArr.Length * 2, stackTop + arcCount));
                    Array.Copy(stackArr, newArr, stackTop);
                    ArrayPool<int>.Shared.Return(stackArr);
                    stackArr = newArr;
                }
                
                for (int i = arcCount - 1; i >= 0; i--)
                {
                    ref FstArc arc = ref arcs[start + i];
                    stackArr[stackTop++] = arc.TargetNodeIndex;
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(stackArr);
        }
        
        return count;
    }
    
    /// <summary>
    /// Counts all outputs from a subtree.
    /// </summary>
    private static int CountOutputs(int startNode, FstNode[] nodes, FstArc[] arcs)
    {
        if (startNode < 0 || startNode >= nodes.Length)
            return 0;
        
        int count = 0;
        var stackArr = ArrayPool<int>.Shared.Rent(64);
        int stackTop = 0;
        
        try
        {
            stackArr[stackTop++] = startNode;
            
            while (stackTop > 0)
            {
                int nodeIndex = stackArr[--stackTop];
                ref FstNode node = ref nodes[nodeIndex];
                
                if (node.IsFinal)
                    count++;
                
                int arcCount = node.ArcCount;
                int start = node.ArcStartIndex;

                // Ensure stack capacity
                if (stackTop + arcCount > stackArr.Length)
                {
                    var newArr = ArrayPool<int>.Shared.Rent(Math.Max(stackArr.Length * 2, stackTop + arcCount));
                    Array.Copy(stackArr, newArr, stackTop);
                    ArrayPool<int>.Shared.Return(stackArr);
                    stackArr = newArr;
                }

                for (int i = 0; i < arcCount; i++)
                {
                    ref FstArc arc = ref arcs[start + i];
                    stackArr[stackTop++] = arc.TargetNodeIndex;
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(stackArr);
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
        if (_forwardNodes.Length == 0) 
            yield break;
        
        // Iterative DFS to avoid stack overflow and allocation
        // Stack stores (NodeIndex, NextArcIndex)
        var stackArr = ArrayPool<(int NodeIndex, int ArcIndex)>.Shared.Rent(64);
        int stackTop = 0;
        var sb = new System.Text.StringBuilder();

        try
        {
            stackArr[stackTop++] = (_forwardRootIndex, 0);

            while (stackTop > 0)
            {
                int frameIndex = stackTop - 1;
                int nodeIndex = stackArr[frameIndex].NodeIndex;
                int arcIndex = stackArr[frameIndex].ArcIndex;
                
                // Check Final (only when first visiting the node)
                if (arcIndex == 0)
                {
                    if (_forwardNodes[nodeIndex].IsFinal)
                    {
                        yield return sb.ToString();
                    }
                }

                // Check children
                // We access array directly to avoid ref local issues across yield boundaries
                int arcCount = _forwardNodes[nodeIndex].ArcCount;

                if (arcIndex < arcCount)
                {
                    int arcStart = _forwardNodes[nodeIndex].ArcStartIndex;
                    ref FstArc arc = ref _forwardArcs[arcStart + arcIndex];
                    
                    // Increment arc index for current node so next time we pick the next child
                    stackArr[frameIndex].ArcIndex++;
                    
                    sb.Append(arc.Label);
                    
                    // Ensure capacity
                    if (stackTop >= stackArr.Length)
                    {
                        var newArr = ArrayPool<(int NodeIndex, int ArcIndex)>.Shared.Rent(stackArr.Length * 2);
                        Array.Copy(stackArr, newArr, stackTop);
                        ArrayPool<(int NodeIndex, int ArcIndex)>.Shared.Return(stackArr);
                        stackArr = newArr;
                    }
                    
                    // Push Child
                    stackArr[stackTop++] = (arc.TargetNodeIndex, 0);
                }
                else
                {
                    // Done with this node
                    stackTop--;
                    if (stackTop > 0)
                    {
                        sb.Length--;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<(int NodeIndex, int ArcIndex)>.Shared.Return(stackArr);
        }
    }

    #endregion
}