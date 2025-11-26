<div align="center">

<img width="512" alt="Infidex" src="https://github.com/user-attachments/assets/e364378f-eba1-4a06-b609-f0dec056d1e3" />

# Infidex

**The high-performance .NET search engine based on pattern recognition**    

[![Infidex](https://shields.io/nuget/v/Infidex?v=304&icon=nuget&label=Infidex)](https://www.nuget.org/packages/Infidex)
[![License:MIT](https://img.shields.io/badge/License-MIT-34D058.svg)](https://opensource.org/license/mit)

Infidex is a search engine based on pattern recognition. Learning from your data, Infidex automatically extracts features like frequency and rarity and embeds them into a multi-dimensional hypersphere for intelligent matching. This enables fuzzy querying with unparalleled typo tolerance, without any manual tuning. Zero dependencies, blazingly fast, built for developers who need search that _just works_.
 
</div>

## ✨ Features

- **Blazingly Fast** - Index thousands of documents per second, search in milliseconds
- **Intelligent Matching** - Finds what you're looking for even with typos and variations
- **Per-Term Coverage** - Ranks documents by how many query terms they match (more terms = higher rank)
- **Rich Filtering** - SQL-like query language (Infiscript) for complex filters
- **Faceted Search** - Build dynamic filters and aggregations
- **Smart Ranking** - Lexicographic (coverage, quality) scoring for principled relevance
- **Multi-Field Search** - Search across multiple fields with configurable weights
- **Incremental Indexing** - Add or update documents without rebuilding the entire index
- **Fully Thread-Safe** - Multiple concurrent readers, writers block readers and other writers
- **Production Ready** - Comprehensive test coverage, clean API, zero dependencies
- **Easy Integration** - Embeds directly into your .NET application

## Quick Start

Install via NuGet:

```bash
dotnet add package Infidex
```

### Basic Search

```csharp
using Infidex;
using Infidex.Core;

// Create search engine
var engine = SearchEngine.CreateDefault();

// Index documents
var documents = new[]
{
    new Document(1L, "The quick brown fox jumps over the lazy dog"),
    new Document(2L, "A journey of a thousand miles begins with a single step"),
    new Document(3L, "To be or not to be that is the question")
};

engine.IndexDocuments(documents);

// Search with typos - still finds matches!
var results = engine.Search("quik fox", maxResults: 10);

foreach (var result in results.Records)
{
    Console.WriteLine($"Doc {result.DocumentId}: Score {result.Score}");
}
```

### Multi-Field Search

```csharp
using Infidex.Api;

// Define fields with weights
var matrix = new DocumentFields();
matrix.AddField("title", "The Matrix", Weight.High);
matrix.AddField("description", "A computer hacker learns about the true nature of reality", Weight.Low);

var inception = new DocumentFields();
inception.AddField("title", "Inception", Weight.High);
inception.AddField("description", "A thief who steals corporate secrets through dream-sharing", Weight.Low);

var movies = new[]
{
    new Document(1L, matrix),
    new Document(2L, inception)
};

engine.IndexDocuments(movies);
```

## Infiscript

Infiscript is a simple filtering language used to write intuitive filters that compile to optimized bytecode:

```csharp
using Infidex.Api;

// Simple comparison
var filter = Filter.Parse("genre = 'Sci-Fi'");

// Boolean logic
var filter = Filter.Parse("genre = 'Sci-Fi' AND year >= 2000");

// Complex expressions with grouping
var filter = Filter.Parse("(genre = 'Fantasy' AND year >= 2000) OR (genre = 'Horror' AND year >= 1980)");

// String operations
var filter = Filter.Parse("title CONTAINS 'matrix'");
var filter = Filter.Parse("title STARTS WITH 'The'");
var filter = Filter.Parse("description LIKE '%dream%'");

// Range checks
var filter = Filter.Parse("year BETWEEN 2000 AND 2020");
var filter = Filter.Parse("rating >= 8.0");

// List membership
var filter = Filter.Parse("genre IN ('Sci-Fi', 'Fantasy', 'Adventure')");

// Null checks
var filter = Filter.Parse("director IS NOT NULL");

// Regex matching
var filter = Filter.Parse("email MATCHES '^[\\w\\.-]+@[\\w\\.-]+\\.\\w+$'");

// Ternary expressions (conditional logic)
var filter = Filter.Parse("age >= 18 ? 'adult' : 'minor'");
var filter = Filter.Parse("score >= 90 ? 'A' : score >= 80 ? 'B' : 'C'");

// Use filters in queries
var query = new Query("matrix", maxResults: 20)
{
    Filter = Filter.Parse("year >= 2000 AND rating > 7.0")
};

var results = engine.Search(query);
```

### Infiscript Operators

Full EBNF specification is available [here](https://github.com/lofcz/Infidex/blob/master/src/Infidex/Api/Infiscript.bnf).

**Comparison:** `=`, `!=`, `<`, `<=`, `>`, `>=`  
**Boolean:** `AND` (or `&&`), `OR` (or `||`), `NOT` (or `!`)  
**String:** `CONTAINS`, `STARTS WITH`, `ENDS WITH`, `LIKE` (% wildcard)  
**Special:** `IN`, `BETWEEN`, `IS NULL`, `IS NOT NULL`, `MATCHES` (regex)  
**Conditional:** `? :` (ternary operator)

All operators are **case-insensitive**. Use parentheses for grouping.

### Bytecode Compilation

Filters compile to portable bytecode for performance and serialization:

```csharp
// Compile once, use many times
var filter = Filter.Parse("genre = 'Sci-Fi' AND year >= 2000");
var bytecode = filter.CompileToBytes();

// Save to disk
File.WriteAllBytes("filter.bin", bytecode);

// Load and use later
var loaded = Filter.FromBytecode(File.ReadAllBytes("filter.bin"));
var query = new Query("space") { CompiledFilterBytecode = bytecode };
```

## Faceted Search & Aggregations

Build dynamic filters and navigate your data:

```csharp
var query = new Query("science fiction", maxResults: 50)
{
    EnableFacets = true
};

var results = engine.Search(query);

// Get facet counts
if (results.Facets != null)
{
    foreach (var (fieldName, values) in results.Facets)
    {
        Console.WriteLine($"\n{fieldName}:");
        foreach (var (value, count) in values)
        {
            Console.WriteLine($"  {value}: {count} documents");
        }
    }
}

// Output:
// genre:
//   Sci-Fi: 15 documents
//   Fantasy: 8 documents
//   Action: 5 documents
// 
// year:
//   2020: 10 documents
//   2019: 8 documents
//   2018: 10 documents
```

## Document Boosting

Increase relevance scores for specific documents:

```csharp
// Boost recent movies
var recentBoost = new Boost(
    Filter.Parse("year >= 2020"),
    BoostStrength.Large  // +20 to score
);

// Boost highly-rated content
var ratingBoost = new Boost(
    Filter.Parse("rating >= 8.0"),
    BoostStrength.Medium  // +10 to score
);

var query = new Query("action movie", maxResults: 20)
{
    EnableBoost = true,
    Boosts = new[] { recentBoost, ratingBoost }
};

var results = engine.Search(query);
```

Boost strengths: `Small` (+5), `Medium` (+10), `Large` (+20), `Extreme` (+40)

## Sorting

Sort results by any field:

```csharp
// Sort by year (descending)
var query = new Query("thriller", maxResults: 20)
{
    SortBy = fields.GetField("year"),
    SortAscending = false
};

// Sort by rating (ascending)
var query = new Query("comedy", maxResults: 20)
{
    SortBy = fields.GetField("rating"),
    SortAscending = true
};
```

## How It Works

Infidex uses a **lexicographic ranking model** where:
- **Precedence** is driven by structural and positional properties (coverage, phrase runs, anchor positions, etc.).
- **Semantic score** is refined using corpus-derived weights (inverse document frequency over character n‑grams), without any per-dataset manual tuning.

Concretely, each query term $q_i$ is assigned a weight

$$
I_i \approx \log_2\frac{N}{\mathrm{df}_i}
$$

where $N$ is the number of documents and $\mathrm{df}_i$ is the document frequency of the term’s character n‑grams. Rarer terms get higher weights and therefore contribute more strongly to coverage and fusion decisions.

### Three-Stage Search Pipeline

**Stage 1: BM25+ Candidate Generation**
- Tokenizes text into character n-grams (2-grams + 3-grams)
- Builds inverted index with document frequencies
- BM25+ scoring backbone with L2-normalized term weights:

$$\text{BM25+}(q, d) = \sum_{t \in q} \text{IDF}(t) \cdot \left( \frac{f(t,d) \cdot (k_1 + 1)}{f(t,d) + k_1 \cdot (1 - b + b \cdot \frac{|d|}{\text{avgdl}})} + \delta \right)$$

Formally, let $V$ be the set of all indexed terms over alphabet $\Sigma$. Infidex builds a deterministic finite-state transducer

$$
T = (Q, \Sigma, \delta, q_0, F, \mu)
$$

such that for each $t \in V$ there is a unique path from $q_0$ to some $q \in F$ labeled by $t$, and $\mu(t) \in \mathbb{N}$ is a term identifier.  
Prefix and suffix queries are then evaluated as:

$$
\mathrm{Pref}(p) = \{\mu(t) \mid t \in V,\ t \text{ has prefix } p\}
$$

$$
\mathrm{Suff}(s) = \{\mu(t) \mid t \in V,\ t \text{ has suffix } s\}
$$

with time complexity $O(|p| + |\mathrm{Pref}(p)|)$ and $O(|s| + |\mathrm{Suff}(s)|)$, respectively.

**Stage 2: Lexical Coverage Analysis**
- Applied to top-K candidates from Stage 1
- Tracks **per-term coverage** for each query word using 5 algorithms:
  - Exact whole-word matching
  - Fuzzy word matching (Damerau–Levenshtein with an edit radius adapted from a binomial typo model)
  - Joined/split word detection
  - Prefix/suffix matching (prefixes weighted higher than suffixes)
  - LCS (Longest Common Subsequence) fallback when no word-level match exists
- For each query term $q_i$, computes per-term coverage:

$$c_i = \min\left(1, \frac{m_i}{|q_i|}\right)$$

where $m_i$ is the number of matched characters for term $i$.

- Derives coordination coverage across all $n$ query terms:

$$C_{\text{coord}} = \frac{1}{n} \sum_{i=1}^{n} c_i$$

- Extracts structural features: phrase runs, anchor token positions, lexical perfection


On top of raw per-term coverage, Infidex tracks how much **information mass** from the query is actually matched:

- For each query term $q_i$, we compute a coverage score $c_i \in [0,1]$ and an information weight $I_i$ as above:
  
  $$
  C_{\text{info}} = \frac{\sum_i c_i I_i}{\sum_i I_i}
  $$
  
This information view is used for two key behaviors:

- **Type-ahead detection**: the last query term is treated as “still being typed” when its information share is small:

  $$
  \frac{I_{\text{last}}}{\sum_i I_i} \le \frac{1}{n+1}
  $$

  where $n$ is the number of unique query terms. Intuitively, the suffix is informationally weaker than an average term, so we avoid over-committing to it.

- **Position-independent precedence boost**: when exactly one term is unmatched, we compare the **fraction of missing terms** ($1 - C_{\text{coord}}$) to the **fraction of missing information** (derived from $C_{\text{info}}$). If we have lost fewer bits of information than raw term coverage suggests, a precedence bit is set so that documents matching the rarer, more informative term outrank those matching only common terms.

$$
\text{termGap} = 1 - C_{\text{coord}}, \qquad
R_{\text{miss}} = \frac{\sum_i (1 - c_i) I_i}{\sum_i I_i}
$$

If $R_{\text{miss}} < \text{termGap}$ (i.e. we have lost fewer bits of information than raw term coverage suggests), a precedence bit is set.

**Stage 3: Lexicographic Score Fusion**

The final **ordering** is a lexicographic triple $(\text{Precedence}, \text{Semantic}, \tau)$, where $\tau$ is an 8-bit tiebreaker.  
Precedence and Semantic are encoded into a 16-bit `ushort`:

$$\text{score} = (\text{Precedence} \ll 8) + \text{Semantic}$$

where:
- **Precedence** (high byte, 8 bits): Discrete match-quality tiers
- **Semantic** (low byte, 8 bits): Continuous similarity within a tier
- **Tiebreaker** $\tau \in [0,255]$: Additional ordering signal used only when both Precedence and Semantic are equal (e.g., derived from word hits or LCS features)

#### Precedence Hierarchy

Documents are ranked by a **strict precedence order**—higher bits always dominate lower bits:

| Bit | Value | Condition | Rationale |
|-----|-------|-----------|-----------|
| 7 | 128 | All query terms found in document | Completeness is paramount |
| 6 | 64 | All terms fully matched (whole word OR exact prefix) | Clean matches beat partial |
| 5 | 32 | **Single-term**: Strict whole-word match<br>**Multi-term**: Lexically perfect doc (every doc token explained by query) | Exact intent signal |
| 4 | 16 | **Single-term**: Perfect doc<br>**Multi-term**: Strict whole-word match | Secondary exactness |
| 3 | 8 | First match at position 0 (title starts with query) | Positional primacy |
| 2 | 4 | Precise prefix match (query is prefix of token) | Partial but anchored |

#### Semantic Score

The semantic component provides smooth differentiation within precedence tiers:

**For single-term queries**

For single-term queries Infidex uses a **heuristic blend** of:

- Per-term coverage $C_{\text{avg}}$ (how completely the query token is matched), and
- A lexical similarity score $L_{\text{lex}}$ that takes the maximum over several signals:

  - $L_{\text{substr}}$: substring containment (with a bias toward matches earlier in the query),
  - $L_{\text{prefix}}$: overlap between query suffix and token prefix,
  - $L_{\text{fuzzy}}$: Damerau–Levenshtein similarity (with transpositions),
  - $L_{\text{2seg}}$: a simple two-segment check for concatenated queries.

The final single-term semantic score is just a convex combination of $C_{\text{avg}}$ and $L_{\text{lex}}$ chosen for practical behavior on real-world data.

**For multi-term queries:**

$$S_{\text{multi}} = \alpha \cdot C_{\text{avg}} + \beta \cdot T_{\text{tfidf}} + \gamma \cdot R_{\text{phrase}}$$

where:
- $C_{\text{avg}}$ = average per-term coverage
- $T_{\text{tfidf}}$ = normalized TF-IDF score from Stage 1
- $R_{\text{phrase}}$ = phrase run bonus (consecutive query terms in document order)
- $\alpha + \beta + \gamma = 1$ ($\alpha$, $\beta$, $\gamma$ are adjustable constants)

#### Two-Segment Alignment (Single-Term Queries)

For queries that appear to be concatenated words, Infidex detects **two-segment alignment**:

Given query $q$ with $|q| \geq 6$, extract:
- Prefix fragment: $q[0:\ell]$ where $\ell = \min(3, |q|/2)$
- Suffix fragment: $q[|q|-\ell:|q|]$

If distinct document tokens $t_i$ and $t_j$ ($i \neq j$) satisfy:
- $t_i$ starts with or is a prefix of the prefix fragment
- $t_j$ ends with or is a suffix of the suffix fragment

Then:

$$L_{\text{2seg}} = \frac{|q[0:\ell]| + |q[|q|-\ell:|q|]|}{|q|}$$

#### Anchor Token Detection (Multi-Term Queries)

For multi-term queries, the **first query token** acts as an anchor when:
1. It has length ≥ 3 characters
2. It appears as a substring in the document text
3. There is a phrase run of ≥ 2 consecutive query terms

This is a pure positional/string rule—no rarity or IDF is used.

#### Final Score Encoding

The 16-bit score component is computed as:

$$\text{score} = (\text{Prec} \ll 8) + \lfloor S \times 255 \rfloor$$

where $S \in [0, 1]$ is the semantic score and $\text{Prec} \in [0, 255]$ is the precedence bitmask.  
The overall ranking key is then the triple $(\text{Prec}, \lfloor S \times 255 \rfloor, \tau)$ ordered lexicographically.

This ensures:
1. **Completeness dominates** (all terms found beats partial matches)
2. **Match quality refines** (whole words beat prefixes beat fuzzy)
3. **Similarity differentiates** (smooth ordering within tiers)

## Persistence

Infidex supports efficient binary serialization for persisting and loading search indexes:

### Saving an Index

```csharp
var engine = SearchEngine.CreateDefault();

// Index your documents
engine.IndexDocuments(documents);

// Save to disk (binary format)
engine.Save("my-index.bin");
```

### Loading an Index

```csharp
// Load from disk with the same configuration
var engine = SearchEngine.Load(
    filePath: "my-index.bin",
    indexSizes: new[] { 3 },
    startPadSize: 2,
    stopPadSize: 0,
    enableCoverage: true
);

// Search immediately (no re-indexing needed)
var results = engine.Search("query text", maxResults: 10);
```

### Async Save/Load

```csharp
// Save asynchronously
await engine.SaveAsync("my-index.bin");

// Load asynchronously
var engine = await SearchEngine.LoadAsync(
    filePath: "my-index.bin",
    indexSizes: new[] { 3 },
    startPadSize: 2,
    stopPadSize: 0
);
```

### What Gets Saved?

The binary format includes:
- All indexed documents (text, metadata, fields)
- Complete inverted index (terms and postings)
- TF-IDF weights (pre-normalized, byte-quantized)
- Document frequencies and statistics
- The FST term index for exact/prefix/suffix lookups
- The short-query prefix index (positional postings for 1–3 character prefixes)
- WordMatcher indices (exact words, LD1 deletions, and affix FST)

On disk, the file is laid out as:
- A fixed-size header (magic, format version, flags, document count, term count, header checksum computed via a simple CRC-style function)
- A length-prefixed data block containing documents, terms, FST, and short-query index, plus a CRC32-style checksum for that block
- A trailing section with the serialized `WordMatcher` data

The header and data block are sufficient to fully reconstruct the `VectorModel`; the trailing section is only used to restore the `WordMatcher`.

**Index Size**: Typically much smaller than source data due to byte-quantized weights and compressed postings. Example: 40k movie titles = < 5 MB index. See [this test](https://github.com/lofcz/Infidex/blob/a60d3a7753cc4bf48a57a34d14a44bfc0d7a7223/src/Infidex.Tests/PersistenceTests.cs#L77-L175).

## Thread Safety & Concurrency

- `SearchEngine.Search` is thread-safe and can be called from many threads concurrently.
- Indexing operations (`IndexDocuments`, `IndexDocumentsAsync`, `IndexDocument`, `CalculateWeights`, `Save`, `Load`) acquire an exclusive writer lock and block other operations while they run.
- Filters and boosts are compiled once and cached in a thread-safe dictionary; execution uses thread-local virtual machines to avoid shared state.
- Share a single `SearchEngine` instance per application/service and let multiple threads use it concurrently.

## Configuration

### Default Configuration (Recommended)
```csharp
var engine = SearchEngine.CreateDefault();
// - Multi-size n-grams: [2, 3]
// - Coverage: Enabled (multi-algorithm model: exact, LD1 fuzzy, prefix/suffix, join/split, LCS)
// - Balanced for speed and accuracy
```

### Minimal Configuration (Speed Priority)
```csharp
var engine = SearchEngine.CreateMinimal();
// - Single n-gram: [3]
// - Coverage: Disabled
// - Faster but less accurate
```

### Custom Configuration
```csharp
var engine = new SearchEngine(
    indexSizes: new[] { 2, 3 },           // Character n-gram sizes
    startPadSize: 2,                       // Pad start of words
    stopPadSize: 0,                        // Pad end of words
    enableCoverage: true,                  // Enable lexical matching
    textNormalizer: TextNormalizer.CreateDefault(),
    tokenizerSetup: TokenizerSetup.CreateDefault(),
    coverageSetup: CoverageSetup.CreateDefault(),
    wordMatcherSetup: new WordMatcherSetup
    {
        SupportLD1 = true,                 // Enable fuzzy matching
        SupportAffix = true,               // Enable prefix/suffix
        MinimumWordSizeExact = 2,
        MaximumWordSizeExact = 50,
        MinimumWordSizeLD1 = 4,
        MaximumWordSizeLD1 = 20
    },
    fieldWeights: new[] { 1.0f, 2.0f }   // Per-field weights
);
```


## Testing

Infidex ships with a comprehensive test suite of 400+ tests, including:
- Multilingual query relevancy tests
- Concurrency tests exercising parallel search, indexing, and save/load patterns
- Persistence, performance, and core API behavior tests

```bash
dotnet test
```

Contributions of additional query relevancy tests from speakers of other languages are very welcome, especially for languages with rich morphology or non-Latin scripts. These tests help keep the ranking model language-agnostic and robust without per-dataset tweaks.

## API Reference

### Core Classes

**`SearchEngine`** - Main search engine  
**`Document`** - Document with ID and fields  
**`Query`** - Search query configuration  
**`Filter`** - Base class for filters  
**`DocumentFields`** - Field schema definition  
**`Boost`** - Boost configuration  

### Key Methods

```csharp
// Indexing
engine.IndexDocuments(IEnumerable<Document> docs)
await engine.IndexDocumentsAsync(IEnumerable<Document> docs)

// Searching  
SearchResult Search(string text, int maxResults = 10)
SearchResult Search(Query query)

// Document management
Document? GetDocument(long documentKey)
List<Document> GetDocuments(long documentKey)  // All segments

// Schema
engine.DocumentFieldSchema = fields;
```

## Examples

Check out [Infidex.Example](https://github.com/lofcz/Infidex/tree/master/src/Infidex.Example) for complete working examples:

- **MovieExample.cs** - Multi-field movie search with faceting
- **FilterExample.cs** - Advanced Infiscript filtering
- **BoostExample.cs** - Document boosting strategies

## License

This library is licensed under the [MIT](https://github.com/lofcz/Infidex/blob/master/LICENSE) license. 💜
