using System.Buffers;
using System.Runtime.CompilerServices;
using Infidex.Core;

namespace Infidex.Indexing;

/// <summary>
/// Dynamic Score-Decomposed Trie for O(|p| + k log k) top-k prefix completion.
/// 
/// Based on: "Heap-like Dynamic Score-Decomposed Tries for Top-k Autocomplete" (Salter)
/// 
/// Key properties:
/// 1. Path decomposition - each node stores full string, branches stored with LCP lengths
/// 2. Heap property - nodes sorted by score both horizontally (among siblings) and vertically
/// 3. Top-k retrieval in O(|p| + k log k) time after locating prefix
/// 
/// Mathematical Foundation (Information Theory):
/// - IDF-weighted scores naturally create power-law distributions
/// - Heap ordering exploits this: highest-scored terms are always accessible in O(1)
/// - Branch points with LCP enable O(1) navigation to relevant subtree
/// 
/// Incremental Support:
/// - Add/Update: O(d log b) where d=depth, b=max branching factor
/// - Thread-safe via reader-writer lock pattern (external synchronization)
/// </summary>
public sealed class ScoreDecomposedTrie
{
    private Node? _root;
    private int _count;
    private readonly object _writeLock = new();
    
    /// <summary>Number of strings in the trie.</summary>
    public int Count => _count;
    
    /// <summary>
    /// A node in the score-decomposed trie.
    /// Each node represents a complete string with its score.
    /// Branch points are sorted by score (descending) to maintain heap property.
    /// </summary>
    private sealed class Node
    {
        public string Key;       // The full string this node represents
        public float Score;      // The score for this string (IDF-weighted)
        public Term? Term;       // Optional: the Term object for this string
        public List<BranchPoint>? BranchPoints; // Sorted by score (descending)
        
        public Node(string key, float score, Term? term = null)
        {
            Key = key;
            Score = score;
            Term = term;
        }
    }
    
    /// <summary>
    /// A branch point stores the LCP (longest common prefix) length with parent
    /// and a reference to the child node.
    /// </summary>
    private readonly struct BranchPoint
    {
        public readonly int LcpLength;  // Length of common prefix with containing node's key
        public readonly Node Node;
        
        public BranchPoint(int lcpLength, Node node)
        {
            LcpLength = lcpLength;
            Node = node;
        }
    }
    
    /// <summary>
    /// Adds or updates a term in the trie.
    /// Maintains heap property after insertion.
    /// Time: O(|term| + d log b) where d=depth, b=branching factor
    /// </summary>
    public void Set(string term, float score, Term? termObj = null)
    {
        if (string.IsNullOrEmpty(term))
            return;
        
        lock (_writeLock)
        {
            if (_root == null)
            {
                _root = new Node(term, score, termObj);
                _count = 1;
                return;
            }
            
            SetInternal(term, score, termObj);
        }
    }
    
    /// <summary>
    /// Batch insert from sorted list (descending by score).
    /// More efficient than individual inserts when building from scratch.
    /// </summary>
    public void BuildFromSorted(IEnumerable<(string term, float score, Term? termObj)> sortedItems)
    {
        lock (_writeLock)
        {
            _root = null;
            _count = 0;
            
            foreach (var (term, score, termObj) in sortedItems)
            {
                if (string.IsNullOrEmpty(term))
                    continue;
                
                if (_root == null)
                {
                    _root = new Node(term, score, termObj);
                    _count = 1;
                }
                else
                {
                    // Simplified insert for pre-sorted data - no promotions needed
                    InsertSorted(term, score, termObj);
                }
            }
        }
    }
    
    /// <summary>
    /// Finds the top-k completions for a given prefix.
    /// Time: O(|prefix| + k log k)
    /// 
    /// Algorithm (from the paper):
    /// 1. Find locus node (highest node representing prefix)
    /// 2. Use priority queue to enumerate nodes in score order
    /// 3. For each extracted node, add its children to queue
    /// </summary>
    public IEnumerable<(string key, float score, Term? term)> FindTopK(string prefix, int k)
    {
        if (_root == null || k <= 0)
            yield break;
        
        // Find locus node
        var locus = FindLocus(prefix, out int prefixLength);
        if (locus == null)
            yield break;
        
        // If locus matches the prefix, yield it first
        if (prefixLength == prefix.Length)
        {
            yield return (locus.Key, locus.Score, locus.Term);
            k--;
            if (k <= 0)
                yield break;
        }
        
        // Use bounded priority queue for top-k enumeration
        // Queue contains (score, branchPoints, index, minLcp)
        var queue = new PriorityQueue<(List<BranchPoint> bp, int index, int minLcp), float>(
            Comparer<float>.Create((a, b) => b.CompareTo(a))); // Max-heap
        
        // Initialize with locus's branch points that have LCP >= prefix length
        if (locus.BranchPoints != null)
        {
            for (int i = 0; i < locus.BranchPoints.Count; i++)
            {
                var bp = locus.BranchPoints[i];
                if (bp.LcpLength >= prefix.Length)
                {
                    // Add first valid branch point
                    yield return (bp.Node.Key, bp.Node.Score, bp.Node.Term);
                    k--;
                    if (k <= 0)
                        yield break;
                    
                    // Add horizontal successor (next sibling with same LCP constraint)
                    for (int j = i + 1; j < locus.BranchPoints.Count; j++)
                    {
                        if (locus.BranchPoints[j].LcpLength >= prefix.Length)
                        {
                            queue.Enqueue((locus.BranchPoints, j, prefix.Length), 
                                locus.BranchPoints[j].Node.Score);
                            break;
                        }
                    }
                    
                    // Add vertical successor (first child)
                    if (bp.Node.BranchPoints != null && bp.Node.BranchPoints.Count > 0)
                    {
                        queue.Enqueue((bp.Node.BranchPoints, 0, 0), 
                            bp.Node.BranchPoints[0].Node.Score);
                    }
                    break;
                }
            }
        }
        
        // Extract nodes in score order
        while (k > 0 && queue.Count > 0)
        {
            var (bp, idx, minLcp) = queue.Dequeue();
            var node = bp[idx].Node;
            
            yield return (node.Key, node.Score, node.Term);
            k--;
            
            if (k <= 0)
                yield break;
            
            // Add horizontal successor
            for (int j = idx + 1; j < bp.Count; j++)
            {
                if (bp[j].LcpLength >= minLcp)
                {
                    queue.Enqueue((bp, j, minLcp), bp[j].Node.Score);
                    break;
                }
            }
            
            // Add vertical successor
            if (node.BranchPoints != null && node.BranchPoints.Count > 0)
            {
                queue.Enqueue((node.BranchPoints, 0, 0), 
                    node.BranchPoints[0].Node.Score);
            }
        }
    }
    
    /// <summary>
    /// Checks if any completion exists for the given prefix.
    /// Time: O(|prefix|)
    /// </summary>
    public bool HasPrefix(string prefix)
    {
        if (_root == null || string.IsNullOrEmpty(prefix))
            return false;
        
        return FindLocus(prefix, out int matched) != null && matched == prefix.Length;
    }
    
    /// <summary>
    /// Clears all entries from the trie.
    /// </summary>
    public void Clear()
    {
        lock (_writeLock)
        {
            _root = null;
            _count = 0;
        }
    }
    
    #region Private Implementation
    
    /// <summary>
    /// Finds the locus node for a prefix - the highest node that represents
    /// a string starting with the prefix.
    /// </summary>
    private Node? FindLocus(string prefix, out int matchedLength)
    {
        matchedLength = 0;
        if (_root == null)
            return null;
        
        Node current = _root;
        int lcp = 0;
        
        while (true)
        {
            // Compute LCP between prefix and current node's key
            int keyLen = current.Key.Length;
            int prefixLen = prefix.Length;
            
            while (lcp < keyLen && lcp < prefixLen && 
                   char.ToLowerInvariant(current.Key[lcp]) == char.ToLowerInvariant(prefix[lcp]))
            {
                lcp++;
            }
            
            // If we've matched the entire prefix, this is the locus
            if (lcp == prefixLen)
            {
                matchedLength = lcp;
                return current;
            }
            
            // If we haven't matched all of current key, check if there's a branch
            if (current.BranchPoints == null)
            {
                matchedLength = lcp;
                return null;
            }
            
            // Find branch point with matching LCP
            Node? nextNode = null;
            foreach (var bp in current.BranchPoints)
            {
                if (bp.LcpLength == lcp)
                {
                    // Check if this branch matches the next character
                    if (bp.Node.Key.Length > lcp && prefix.Length > lcp &&
                        char.ToLowerInvariant(bp.Node.Key[lcp]) == char.ToLowerInvariant(prefix[lcp]))
                    {
                        nextNode = bp.Node;
                        break;
                    }
                }
                else if (bp.LcpLength > lcp)
                {
                    // Branch points are sorted, so we can stop here
                    break;
                }
            }
            
            if (nextNode == null)
            {
                matchedLength = lcp;
                return current.Key.Length >= prefix.Length ? current : null;
            }
            
            current = nextNode;
        }
    }
    
    /// <summary>
    /// Simplified insert for pre-sorted data (score descending).
    /// Nodes are always appended, no promotions needed.
    /// </summary>
    private void InsertSorted(string term, float score, Term? termObj)
    {
        Node current = _root!;
        int lcp = 0;
        
        while (true)
        {
            // Compute LCP with current node
            int keyLen = current.Key.Length;
            int termLen = term.Length;
            
            while (lcp < keyLen && lcp < termLen && 
                   char.ToLowerInvariant(current.Key[lcp]) == char.ToLowerInvariant(term[lcp]))
            {
                lcp++;
            }
            
            // Find or create branch point
            current.BranchPoints ??= new List<BranchPoint>();
            
            // Since data is sorted, new node is always appended
            Node? existing = null;
            foreach (var bp in current.BranchPoints)
            {
                if (bp.LcpLength == lcp && bp.Node.Key.Length > lcp && term.Length > lcp &&
                    char.ToLowerInvariant(bp.Node.Key[lcp]) == char.ToLowerInvariant(term[lcp]))
                {
                    existing = bp.Node;
                    break;
                }
            }
            
            if (existing != null)
            {
                current = existing;
                continue;
            }
            
            // Create new node and append (maintains sorted order for pre-sorted input)
            var newNode = new Node(term, score, termObj);
            current.BranchPoints.Add(new BranchPoint(lcp, newNode));
            _count++;
            return;
        }
    }
    
    /// <summary>
    /// Full insert with promotion support for unsorted data.
    /// </summary>
    private void SetInternal(string term, float score, Term? termObj)
    {
        Node current = _root!;
        List<BranchPoint>? currentBranchPoints = null;
        int currentIndex = -1;
        int lcp = 0;
        
        while (true)
        {
            // Compute LCP with current node
            int keyLen = current.Key.Length;
            int termLen = term.Length;
            
            while (lcp < keyLen && lcp < termLen && 
                   char.ToLowerInvariant(current.Key[lcp]) == char.ToLowerInvariant(term[lcp]))
            {
                lcp++;
            }
            
            // Case 1: Exact match found
            if (lcp == termLen && lcp == keyLen)
            {
                // Update existing node
                current.Score = score;
                current.Term = termObj;
                
                // May need to bubble up if score increased
                if (currentBranchPoints != null && currentIndex >= 0)
                {
                    BubbleUp(currentBranchPoints, currentIndex);
                }
                return;
            }
            
            // Case 2: Score is higher than current - need to promote
            if (score > current.Score)
            {
                PromoteNode(term, score, termObj, current, currentBranchPoints, currentIndex, lcp);
                _count++;
                return;
            }
            
            // Case 3: Continue traversal or create new branch
            current.BranchPoints ??= new List<BranchPoint>();
            
            // Find existing branch with same LCP and matching next character
            int branchIndex = -1;
            for (int i = 0; i < current.BranchPoints.Count; i++)
            {
                var bp = current.BranchPoints[i];
                if (bp.LcpLength == lcp)
                {
                    if (bp.Node.Key.Length > lcp && term.Length > lcp &&
                        char.ToLowerInvariant(bp.Node.Key[lcp]) == char.ToLowerInvariant(term[lcp]))
                    {
                        branchIndex = i;
                        break;
                    }
                }
            }
            
            if (branchIndex >= 0)
            {
                // Continue down existing branch
                currentBranchPoints = current.BranchPoints;
                currentIndex = branchIndex;
                current = current.BranchPoints[branchIndex].Node;
                continue;
            }
            
            // Create new branch point, maintaining score order
            var newNode = new Node(term, score, termObj);
            InsertBranchPointSorted(current.BranchPoints, new BranchPoint(lcp, newNode));
            _count++;
            return;
        }
    }
    
    /// <summary>
    /// Promotes a node when a higher-scored term is inserted.
    /// The existing node becomes a child of the new node.
    /// </summary>
    private void PromoteNode(string term, float score, Term? termObj, 
        Node current, List<BranchPoint>? parentBps, int parentIndex, int lcp)
    {
        var newNode = new Node(term, score, termObj);
        
        // The current node becomes a child of the new node
        newNode.BranchPoints = new List<BranchPoint> { new BranchPoint(lcp, current) };
        
        // Move branch points with LCP < lcp to new node
        if (current.BranchPoints != null)
        {
            var toMove = new List<BranchPoint>();
            var toKeep = new List<BranchPoint>();
            
            foreach (var bp in current.BranchPoints)
            {
                if (bp.LcpLength < lcp)
                {
                    toMove.Add(bp);
                }
                else
                {
                    toKeep.Add(bp);
                }
            }
            
            if (toMove.Count > 0)
            {
                foreach (var bp in toMove)
                {
                    InsertBranchPointSorted(newNode.BranchPoints, bp);
                }
                current.BranchPoints = toKeep.Count > 0 ? toKeep : null;
            }
        }
        
        // Replace current in parent
        if (parentBps != null && parentIndex >= 0)
        {
            var oldBp = parentBps[parentIndex];
            parentBps[parentIndex] = new BranchPoint(oldBp.LcpLength, newNode);
            BubbleUp(parentBps, parentIndex);
        }
        else
        {
            // Replacing root
            _root = newNode;
        }
    }
    
    /// <summary>
    /// Inserts a branch point maintaining descending score order.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InsertBranchPointSorted(List<BranchPoint> bps, BranchPoint newBp)
    {
        float newScore = newBp.Node.Score;
        
        // Binary search for insertion point
        int lo = 0, hi = bps.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (bps[mid].Node.Score > newScore)
                lo = mid + 1;
            else
                hi = mid;
        }
        
        bps.Insert(lo, newBp);
    }
    
    /// <summary>
    /// Bubbles up a branch point if its score increased.
    /// </summary>
    private static void BubbleUp(List<BranchPoint> bps, int index)
    {
        if (index <= 0 || bps.Count <= 1)
            return;
        
        var current = bps[index];
        float score = current.Node.Score;
        
        // Bubble up while score is higher than predecessor
        while (index > 0 && bps[index - 1].Node.Score < score)
        {
            bps[index] = bps[index - 1];
            index--;
        }
        
        bps[index] = current;
    }
    
    #endregion
}

