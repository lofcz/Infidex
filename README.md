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

### Three-Stage Search Pipeline

**Stage 1: TF-IDF Relevancy Ranking**
- Tokenizes text into character n-grams (2-grams + 3-grams)
- Builds inverted index with document frequencies
- Calculates relevancy scores using TF-IDF with L2 normalization
- Ultra-fast with byte-quantized weights (4x memory savings)

**Stage 2: Per-Term Coverage Analysis**
- Applied to top-K candidates from Stage 1
- Tracks **per-term coverage** for each query word using 5 algorithms:
  - Exact whole-word matching
  - Fuzzy word matching (adaptive, length-aware Levenshtein)
  - Joined/split word detection
  - Prefix/suffix matching (prefixes weighted higher than suffixes)
  - LCS (Longest Common Subsequence) fallback when no word-level match exists
- For each query term $q_i$, computes per-term coverage:

$$c_i = \min\left(1, \frac{m_i}{|q_i|}\right)$$

where $m_i$ is the number of matched characters for term $i$.

- Derives coordination coverage across all $n$ query terms:

$$C_{\text{coord}} = \frac{1}{n} \sum_{i=1}^{n} c_i$$

**Stage 3: Lexicographic Score Fusion**
- Let $Q \in [0,1]$ be the normalized match quality (max of TF-IDF and coverage scores)
- Uses **lexicographic ordering** $(C_{\text{coord}}, \text{Prec}, Q)$:
  1. **Coverage Tier**: Documents matching more query terms **always** outrank those matching fewer.
  2. **Precedence Bitmask**: Context-aware signal strength model breaks ties within coverage tiers.
     - **Bit 7 (128)**: All Terms Found
     - **Bit 6 (64)**: All Terms Fully Matched (Whole OR Exact Prefix)
     - **Bit 5 (32)**: Strict Whole Word (for single-term) / Perfect Doc (for multi-term)
     - **Bit 4 (16)**: Perfect Doc (for single-term) / Strict Whole Word (for multi-term)
     - **Bit 3 (8)**: First Match at Index 0 (Starts with query)
     - **Bit 2 (4)**: Precise Prefix Match (Start of Token)
  3. **Quality Score**: TF-IDF quality $Q$ breaks remaining ties.

- Final score encoded as a `ushort` (16-bit):

$$\text{score} = (\text{Prec} \ll 8) \mid ((\lfloor C_{\text{coord}} \times 63 \rfloor \ll 2) \mid \lfloor Q \times 3 \rfloor)$$

This ensures a deterministic ranking where:
1. Completeness rules (Coverage)
2. Signal strength refines (Precedence)
3. Textual similarity differentiates (Quality)


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
    stopTermLimit: 1_250_000,             // Max terms before stop-word filtering
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

Comprehensive test suite with 300+ tests:

```bash
dotnet test
```

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
