using System.Buffers;
using System.Runtime.CompilerServices;
using Infidex.Core;
using Infidex.Metrics;

namespace Infidex.Indexing;

/// <summary>
/// Depth-First Fuzzy Autocomplete (DFA) algorithm for efficient top-k fuzzy search.
/// 
/// Based on: "Towards efficient top-k fuzzy auto-completion queries" 
/// (AbdelNaby et al., Alexandria Engineering Journal, 2020)
/// 
/// Key Innovation:
/// - Uses min-heap ordered by (prefix_edit_distance, -prefix_length_matched)
/// - Depth-first traversal prioritizes nodes closer to completion
/// - Early termination when k results found
/// - 5-10x faster than breadth-first approaches (META algorithm)
/// 
/// Mathematical Foundation:
/// - Prefix Edit Distance (PED): min edit distance between query and any prefix of word
/// - PED enables type-ahead semantics: "algo" matches "algorithm" with PED=0
/// - Heap ordering by PED ensures globally optimal top-k selection
/// 
/// Complexity:
/// - Time: O(k * average_depth) vs O(δ * all_candidates) for breadth-first
/// - Space: O(k) for the priority queue
/// </summary>
public sealed class DepthFirstFuzzySearch
{
    private readonly TrieNode _root = new();
    private int _termCount;
    
    /// <summary>Number of terms indexed.</summary>
    public int TermCount => _termCount;
    
    /// <summary>
    /// Trie node for fuzzy search.
    /// Each node represents a character in the path from root.
    /// </summary>
    private sealed class TrieNode
    {
        public Dictionary<char, TrieNode>? Children;
        public List<(string word, float score, Term? term)>? Completions;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TrieNode GetOrCreateChild(char c)
        {
            Children ??= new Dictionary<char, TrieNode>();
            if (!Children.TryGetValue(c, out var child))
            {
                child = new TrieNode();
                Children[c] = child;
            }
            return child;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TrieNode? GetChild(char c)
        {
            return Children?.GetValueOrDefault(c);
        }
    }
    
    /// <summary>
    /// A matching node in the DFA algorithm.
    /// Tracks the state of fuzzy matching during depth-first traversal.
    /// </summary>
    private readonly struct MatchingNode : IComparable<MatchingNode>
    {
        public readonly TrieNode Node;
        public readonly int QueryIndex;          // Current position in query being matched
        public readonly int PrefixEditDistance;  // Edit distance so far
        public readonly int Depth;               // Depth in trie (characters matched)
        public readonly float Score;             // Combined score for heap ordering
        
        public MatchingNode(TrieNode node, int queryIndex, int ped, int depth)
        {
            Node = node;
            QueryIndex = queryIndex;
            PrefixEditDistance = ped;
            Depth = depth;
            // Score combines PED (lower better) and depth (higher better for tie-breaking)
            // This implements the depth-first strategy from the DFA paper
            Score = ped * 1000f - depth;
        }
        
        public int CompareTo(MatchingNode other)
        {
            // Min-heap by PED, then max by depth (depth-first)
            int cmp = PrefixEditDistance.CompareTo(other.PrefixEditDistance);
            if (cmp != 0) return cmp;
            return other.Depth.CompareTo(Depth); // Higher depth = better
        }
    }
    
    /// <summary>
    /// Adds a term to the fuzzy search index.
    /// </summary>
    public void Add(string term, float score, Term? termObj = null)
    {
        if (string.IsNullOrEmpty(term))
            return;
        
        TrieNode current = _root;
        string normalized = term.ToLowerInvariant();
        
        foreach (char c in normalized)
        {
            current = current.GetOrCreateChild(c);
        }
        
        current.Completions ??= new List<(string, float, Term?)>();
        current.Completions.Add((term, score, termObj));
        _termCount++;
    }
    
    /// <summary>
    /// Finds top-k fuzzy completions for a query using depth-first traversal.
    /// 
    /// Algorithm (DFA from the paper):
    /// 1. Initialize min-heap with root node
    /// 2. Pop minimum PED node from heap
    /// 3. If node reaches end of query, yield its completions
    /// 4. Otherwise, expand children and add to heap
    /// 5. Repeat until k results found or heap empty
    /// 
    /// The key insight: ordering heap by (PED, -depth) causes depth-first
    /// behavior that reaches completions faster than breadth-first.
    /// </summary>
    /// <param name="query">Search query</param>
    /// <param name="k">Number of results to return</param>
    /// <param name="maxEditDistance">Maximum allowed edit distance (δ)</param>
    /// <returns>Top-k fuzzy completions ordered by (PED, score)</returns>
    public IEnumerable<(string word, float score, int editDistance, Term? term)> FindTopK(
        string query, 
        int k, 
        int maxEditDistance = -1)
    {
        if (string.IsNullOrEmpty(query) || k <= 0)
            yield break;
        
        // Dynamic threshold based on query length (from Bast & Celikik 2011)
        if (maxEditDistance < 0)
            maxEditDistance = LevenshteinDistance.GetDynamicThreshold(query.Length);
        
        string queryLower = query.ToLowerInvariant();
        int queryLen = queryLower.Length;
        
        // Priority queue: min-heap by (PED, -depth)
        var heap = new PriorityQueue<MatchingNode, float>();
        
        // Initialize with root node
        heap.Enqueue(new MatchingNode(_root, 0, 0, 0), 0);
        
        // Track results and visited states
        var results = new List<(string word, float score, int ped, Term? term)>();
        var visited = new HashSet<(TrieNode, int, int)>(); // (node, queryIndex, ped)
        
        while (heap.Count > 0 && results.Count < k)
        {
            var mn = heap.Dequeue();
            
            // Skip if we've exceeded max edit distance
            if (mn.PrefixEditDistance > maxEditDistance)
                continue;
            
            // Skip duplicate states
            var state = (mn.Node, mn.QueryIndex, mn.PrefixEditDistance);
            if (!visited.Add(state))
                continue;
            
            // If we've processed the entire query, collect completions
            if (mn.QueryIndex >= queryLen)
            {
                // This node and all descendants are valid completions
                foreach (var completion in CollectCompletions(mn.Node, mn.PrefixEditDistance))
                {
                    results.Add(completion);
                    if (results.Count >= k)
                        break;
                }
                continue;
            }
            
            // Also check if current node has completions (for prefix matching)
            if (mn.Node.Completions != null)
            {
                // Calculate PED from current position to end of query
                int remainingPed = queryLen - mn.QueryIndex;
                int totalPed = mn.PrefixEditDistance + remainingPed;
                
                if (totalPed <= maxEditDistance)
                {
                    foreach (var (word, score, term) in mn.Node.Completions)
                    {
                        results.Add((word, score, totalPed, term));
                        if (results.Count >= k)
                            break;
                    }
                }
            }
            
            if (results.Count >= k)
                break;
            
            char queryChar = queryLower[mn.QueryIndex];
            
            // Expand children - this is where depth-first magic happens
            if (mn.Node.Children != null)
            {
                foreach (var (childChar, childNode) in mn.Node.Children)
                {
                    // Case 1: Exact match - no edit cost
                    if (childChar == queryChar)
                    {
                        var next = new MatchingNode(
                            childNode, 
                            mn.QueryIndex + 1, 
                            mn.PrefixEditDistance, 
                            mn.Depth + 1);
                        heap.Enqueue(next, next.Score);
                    }
                    
                    // Case 2: Substitution - edit cost 1
                    if (mn.PrefixEditDistance + 1 <= maxEditDistance)
                    {
                        if (childChar != queryChar)
                        {
                            var next = new MatchingNode(
                                childNode,
                                mn.QueryIndex + 1,
                                mn.PrefixEditDistance + 1,
                                mn.Depth + 1);
                            heap.Enqueue(next, next.Score);
                        }
                        
                        // Case 3: Insertion in document (skip this trie char)
                        var insertNext = new MatchingNode(
                            childNode,
                            mn.QueryIndex,
                            mn.PrefixEditDistance + 1,
                            mn.Depth + 1);
                        heap.Enqueue(insertNext, insertNext.Score);
                    }
                }
            }
            
            // Case 4: Deletion from document (skip this query char)
            if (mn.PrefixEditDistance + 1 <= maxEditDistance)
            {
                var deleteNext = new MatchingNode(
                    mn.Node,
                    mn.QueryIndex + 1,
                    mn.PrefixEditDistance + 1,
                    mn.Depth);
                heap.Enqueue(deleteNext, deleteNext.Score);
            }
        }
        
        // Sort results by (PED, then by score descending)
        results.Sort((a, b) =>
        {
            int pedCmp = a.ped.CompareTo(b.ped);
            if (pedCmp != 0) return pedCmp;
            return b.score.CompareTo(a.score); // Higher score is better
        });
        
        foreach (var result in results.Take(k))
        {
            yield return result;
        }
    }
    
    /// <summary>
    /// Collects all completions from a node and its descendants.
    /// Ordered by score (descending).
    /// </summary>
    private IEnumerable<(string word, float score, int ped, Term? term)> CollectCompletions(
        TrieNode node, 
        int basePed)
    {
        var stack = new Stack<TrieNode>();
        stack.Push(node);
        
        var completions = new List<(string word, float score, int ped, Term? term)>();
        
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            
            if (current.Completions != null)
            {
                foreach (var (word, score, term) in current.Completions)
                {
                    completions.Add((word, score, basePed, term));
                }
            }
            
            if (current.Children != null)
            {
                foreach (var child in current.Children.Values)
                {
                    stack.Push(child);
                }
            }
        }
        
        // Sort by score descending
        completions.Sort((a, b) => b.score.CompareTo(a.score));
        
        return completions;
    }
    
    /// <summary>
    /// Checks if any completion exists within edit distance threshold.
    /// Faster than FindTopK when you only need existence check.
    /// </summary>
    public bool HasFuzzyMatch(string query, int maxEditDistance = -1)
    {
        if (string.IsNullOrEmpty(query))
            return false;
        
        if (maxEditDistance < 0)
            maxEditDistance = LevenshteinDistance.GetDynamicThreshold(query.Length);
        
        string queryLower = query.ToLowerInvariant();
        
        // Simple DFS with early termination
        return HasFuzzyMatchDfs(_root, queryLower, 0, 0, maxEditDistance);
    }
    
    private bool HasFuzzyMatchDfs(TrieNode node, string query, int qi, int ped, int maxPed)
    {
        if (ped > maxPed)
            return false;
        
        // If we've consumed the entire query, any completion is valid
        if (qi >= query.Length)
        {
            // Check if this node or any descendant has completions
            return HasAnyCompletion(node);
        }
        
        // Check current node's completions (prefix match)
        if (node.Completions != null && ped + (query.Length - qi) <= maxPed)
            return true;
        
        if (node.Children == null)
            return false;
        
        char qc = query[qi];
        
        // Try exact match first (most likely to succeed)
        if (node.Children.TryGetValue(qc, out var exactChild))
        {
            if (HasFuzzyMatchDfs(exactChild, query, qi + 1, ped, maxPed))
                return true;
        }
        
        // Try edit operations if budget allows
        if (ped + 1 <= maxPed)
        {
            // Deletion (skip query char)
            if (HasFuzzyMatchDfs(node, query, qi + 1, ped + 1, maxPed))
                return true;
            
            // Substitution and insertion
            foreach (var (childChar, childNode) in node.Children)
            {
                if (childChar == qc)
                    continue; // Already tried exact match
                
                // Substitution
                if (HasFuzzyMatchDfs(childNode, query, qi + 1, ped + 1, maxPed))
                    return true;
                
                // Insertion (skip trie char)
                if (HasFuzzyMatchDfs(childNode, query, qi, ped + 1, maxPed))
                    return true;
            }
        }
        
        return false;
    }
    
    private static bool HasAnyCompletion(TrieNode node)
    {
        if (node.Completions != null && node.Completions.Count > 0)
            return true;
        
        if (node.Children == null)
            return false;
        
        foreach (var child in node.Children.Values)
        {
            if (HasAnyCompletion(child))
                return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Clears all indexed terms.
    /// </summary>
    public void Clear()
    {
        _root.Children?.Clear();
        _root.Completions?.Clear();
        _termCount = 0;
    }
}

