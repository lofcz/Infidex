using Infidex.Core;

namespace Infidex.Indexing;

/// <summary>
/// Compact trie for O(|prefix|) term prefix lookups.
/// Used to accelerate short query searches by replacing linear term scans.
/// 
/// Design: Array-based children for fast lookup (char -> index mapping).
/// Memory: ~256 bytes per node overhead, but most nodes have sparse children.
/// </summary>
internal sealed class TermPrefixTrie
{
    private readonly Node _root = new();
    private int _termCount;
    
    /// <summary>Number of terms indexed in the trie.</summary>
    public int TermCount => _termCount;
    
    /// <summary>
    /// Adds a term to the trie.
    /// </summary>
    public void Add(Term term)
    {
        if (term.Text == null) return;
        
        Node current = _root;
        foreach (char c in term.Text)
        {
            current = current.GetOrCreateChild(c);
        }
        
        // Store term at leaf (may have multiple terms with same text from different shingle sizes)
        current.Terms ??= new List<Term>(1);
        current.Terms.Add(term);
        _termCount++;
    }
    
    /// <summary>
    /// Finds all terms that start with the given prefix.
    /// Returns empty enumerable if no matches.
    /// Complexity: O(|prefix| + k) where k is the number of matching terms.
    /// </summary>
    public IEnumerable<Term> FindByPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            yield break;
        
        // Navigate to prefix node
        Node? current = _root;
        foreach (char c in prefix)
        {
            current = current.GetChild(c);
            if (current == null)
                yield break;
        }
        
        // Collect all terms in subtree
        foreach (var term in CollectTerms(current))
        {
            yield return term;
        }
    }
    
    /// <summary>
    /// Checks if any term starts with the given prefix.
    /// Faster than FindByPrefix when you only need existence check.
    /// </summary>
    public bool HasPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return false;
        
        Node? current = _root;
        foreach (char c in prefix)
        {
            current = current.GetChild(c);
            if (current == null)
                return false;
        }
        return true;
    }
    
    /// <summary>
    /// Clears all terms from the trie.
    /// </summary>
    public void Clear()
    {
        _root.Children?.Clear();
        _root.Terms?.Clear();
        _termCount = 0;
    }
    
    private static IEnumerable<Term> CollectTerms(Node node)
    {
        // Yield terms at this node
        // Take snapshot to avoid "collection modified during enumeration" if trie is being rebuilt
        var terms = node.Terms;
        if (terms != null)
        {
            // Snapshot the list to avoid concurrent modification issues
            Term[] termsSnapshot = terms.ToArray();
            foreach (var term in termsSnapshot)
            {
                yield return term;
            }
        }
        
        // Recursively collect from children
        var children = node.Children;
        if (children != null)
        {
            // Snapshot the values to avoid concurrent modification issues
            Node[] childrenSnapshot = children.Values.ToArray();
            foreach (var child in childrenSnapshot)
            {
                foreach (var term in CollectTerms(child))
                {
                    yield return term;
                }
            }
        }
    }
    
    /// <summary>
    /// Trie node with lazy child allocation for memory efficiency.
    /// Uses Dictionary for children since most nodes have sparse child sets.
    /// </summary>
    private sealed class Node
    {
        public Dictionary<char, Node>? Children;
        public List<Term>? Terms;
        
        public Node GetOrCreateChild(char c)
        {
            Children ??= new Dictionary<char, Node>();
            
            if (!Children.TryGetValue(c, out Node? child))
            {
                child = new Node();
                Children[c] = child;
            }
            return child;
        }
        
        public Node? GetChild(char c)
        {
            if (Children == null) return null;
            Children.TryGetValue(c, out Node? child);
            return child;
        }
    }
}

