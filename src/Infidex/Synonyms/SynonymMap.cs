namespace Infidex.Synonyms;

/// <summary>
/// Stores synonym mappings for query expansion.
/// Thread-safe for concurrent reads after initialization.
/// </summary>
public class SynonymMap
{
    private readonly Dictionary<string, HashSet<string>> _synonyms;
    
    // Disjoint-set (union–find) structure for canonicalization:
    // each term belongs to an equivalence class represented by a canonical root.
    // Keys are already normalized to lower-invariant strings.
    private readonly Dictionary<string, string> _parent;
    private readonly Dictionary<string, int> _rank;
    
    public SynonymMap()
    {
        _synonyms = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        _parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Adds a bidirectional synonym relationship.
    /// Example: AddSynonym("car", "automobile") means "car" ↔ "automobile"
    /// </summary>
    public void AddSynonym(string term1, string term2)
    {
        if (string.IsNullOrWhiteSpace(term1) || string.IsNullOrWhiteSpace(term2))
            return;
            
        term1 = term1.Trim().ToLowerInvariant();
        term2 = term2.Trim().ToLowerInvariant();
        
        if (term1 == term2)
            return;
        
        // Add term2 to term1's synonym set
        if (!_synonyms.TryGetValue(term1, out var set1))
        {
            set1 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _synonyms[term1] = set1;
        }
        set1.Add(term2);
        
        // Add term1 to term2's synonym set (bidirectional)
        if (!_synonyms.TryGetValue(term2, out var set2))
        {
            set2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _synonyms[term2] = set2;
        }
        set2.Add(term1);
        
        // Merge the two terms into the same canonical equivalence class.
        Union(term1, term2);
    }
    
    /// <summary>
    /// Adds a group of synonyms where each term is a synonym of all others.
    /// Example: AddSynonymGroup("car", "automobile", "vehicle")
    /// </summary>
    public void AddSynonymGroup(params string[] terms)
    {
        if (terms == null || terms.Length < 2)
            return;
            
        for (int i = 0; i < terms.Length; i++)
        {
            for (int j = i + 1; j < terms.Length; j++)
            {
                AddSynonym(terms[i], terms[j]);
            }
        }
    }
    
    /// <summary>
    /// Gets all synonyms for a given term.
    /// Returns empty set if no synonyms found.
    /// </summary>
    public IReadOnlySet<string> GetSynonyms(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return new HashSet<string>();
            
        term = term.Trim().ToLowerInvariant();
        
        if (_synonyms.TryGetValue(term, out var synonyms))
            return synonyms;
            
        return new HashSet<string>();
    }
    
    /// <summary>
    /// Checks if a term has any synonyms.
    /// </summary>
    public bool HasSynonyms(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return false;
            
        term = term.Trim().ToLowerInvariant();
        return _synonyms.ContainsKey(term);
    }
    
    /// <summary>
    /// Gets the total number of terms with synonyms.
    /// </summary>
    public int Count => _synonyms.Count;
    
    /// <summary>
    /// Clears all synonym mappings.
    /// </summary>
    public void Clear()
    {
        _synonyms.Clear();
        _parent.Clear();
        _rank.Clear();
    }
    
    /// <summary>
    /// Returns the canonical representative for a term if it participates in a
    /// synonym group; otherwise returns the term itself (normalized).
    /// 
    /// Example: if "car", "automobile" and "auto" form one group and "car" is
    /// chosen as canonical, GetCanonical("automobile") and GetCanonical("auto")
    /// both return "car".
    /// </summary>
    public string GetCanonical(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return string.Empty;

        term = term.Trim().ToLowerInvariant();

        // If we have never seen this term in any synonym relationship, it is
        // its own canonical representative.
        if (!_parent.ContainsKey(term))
            return term;

        return Find(term);
    }

    /// <summary>
    /// Canonicalizes all word tokens in a text by replacing any token that has
    /// a synonym with its canonical representative. Delimiters are preserved as-is.
    /// 
    /// This is intended for use in indexing and query analysis so that all
    /// equivalent surface forms collapse to a single internal term.
    /// </summary>
    /// <param name="text">Input text (typically already normalized / lowercased).</param>
    /// <param name="delimiters">Word delimiters used by the tokenizer.</param>
    public string CanonicalizeText(string text, char[] delimiters)
    {
        if (string.IsNullOrEmpty(text) || delimiters.Length == 0 || _parent.Count == 0)
            return text;

        HashSet<char> delimiterSet = new HashSet<char>(delimiters);
        var builder = new System.Text.StringBuilder(text.Length);

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (delimiterSet.Contains(c))
            {
                // Preserve delimiter characters exactly.
                builder.Append(c);
                i++;
                continue;
            }

            // Accumulate a token until the next delimiter.
            int start = i;
            while (i < text.Length && !delimiterSet.Contains(text[i]))
            {
                i++;
            }

            string token = text.Substring(start, i - start);
            string canonical = GetCanonical(token);
            builder.Append(canonical);
        }

        return builder.ToString();
    }

    /// <summary>
    /// True if there is at least one canonical synonym relationship configured.
    /// </summary>
    public bool HasCanonicalMappings => _parent.Count > 0;

    #region Union-find helpers

    private void EnsureNode(string term)
    {
        if (!_parent.ContainsKey(term))
        {
            _parent[term] = term;
            _rank[term] = 0;
        }
    }

    private string Find(string term)
    {
        EnsureNode(term);

        string parent = _parent[term];
        if (!string.Equals(parent, term, StringComparison.OrdinalIgnoreCase))
        {
            _parent[term] = Find(parent);
        }

        return _parent[term];
    }

    private void Union(string term1, string term2)
    {
        EnsureNode(term1);
        EnsureNode(term2);

        string root1 = Find(term1);
        string root2 = Find(term2);

        if (string.Equals(root1, root2, StringComparison.OrdinalIgnoreCase))
            return;

        // Prefer a longer, more informative surface form as the canonical
        // representative if lengths tie, fall back to deterministic lexicographic order.
        string canonicalRoot;
        string otherRoot;

        if (root1.Length != root2.Length)
        {
            canonicalRoot = root1.Length >= root2.Length ? root1 : root2;
            otherRoot = ReferenceEquals(canonicalRoot, root1) ? root2 : root1;
        }
        else
        {
            canonicalRoot = string.CompareOrdinal(root1, root2) <= 0 ? root1 : root2;
            otherRoot = string.Equals(canonicalRoot, root1, StringComparison.OrdinalIgnoreCase)
                ? root2
                : root1;
        }

        // Point the non-canonical root at the canonical root. Any existing
        // members of that set will eventually compress to the canonical root
        // via Find(), so we don't need to touch them here.
        _parent[otherRoot] = canonicalRoot;
    }

    #endregion
}

