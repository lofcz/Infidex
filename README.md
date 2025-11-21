<div align="center">

<img width="512" alt="Infidex" src="https://github.com/user-attachments/assets/e364378f-eba1-4a06-b609-f0dec056d1e3" />

# Infidex

**A .NET search engine with TF-IDF ranking, lexical Coverage, and VM-interpreted filtering**    

[![Infidex](https://shields.io/nuget/v/Infidex?v=304&icon=nuget&label=Infidex)](https://www.nuget.org/packages/Infidex)
[![License:MIT](https://img.shields.io/badge/License-MIT-34D058.svg)](https://opensource.org/license/mit)

Infidex is a search engine based on pattern recognition. Learning from your data, Infidex automatically extracts features like frequency and rarity and embeds them into a multi-dimensional hypersphere for intelligent matching. This enables fuzzy querying with unparalleled typo tolerance, without any manual tuning. Zero dependencies, blazingly fast, built for developers who need search that _just works_.
 
</div>

## ✨ Features

- **Two-Stage Architecture**: Combines relevancy ranking (Stage 1) with coverage matching (Stage 2)
- **TF-IDF Vector Model**: Classic information retrieval with L2-normalized vectors
- **Multi-Size N-grams**: Character n-grams (2-3 or configurable) for morphological robustness
- **Byte Quantization**: 4x memory savings by storing weights as bytes (0-255)
- **Bucket-Based Sorting**: O(1) insertion and O(n) top-K retrieval using score buckets
- **5 Lexical Matching Algorithms**:
  - Exact word matching
  - Fuzzy matching (Levenshtein distance ≤ 1)
  - Joined/split word detection
  - Prefix/suffix matching
  - LCS (Longest Common Subsequence)
- **Bit-Parallel Levenshtein**: Ultra-fast edit distance for strings ≤64 characters

## Quick Start

Install the library:

```
dotnet add package infidex
```

Create an index, add documents, query it:

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

// Search
var results = engine.Search("quick fox", maxResults: 10);

foreach (var result in results.Results)
{
    Console.WriteLine($"Doc {result.DocumentId}: Score {result.Score}");
}
```

## Architecture

### Stage 1: Relevancy Ranking (TF-IDF)

1. **Tokenization**: Text → multi-size character n-grams (e.g., 2-grams + 3-grams)
2. **Indexing**: Build inverted index with document frequencies
3. **Weight Calculation**: 
   - Formula: `IDF = 1 + log₁₀(N × tf / df)`
   - L2 normalization across all terms
   - Byte quantization (0-255)
4. **Search**: Cosine similarity via dot product

### Stage 2: Coverage (Lexical Matching)

Applied to top-K candidates from Stage 1 (default K=500):

1. **Exact Word Matching**: Complete word matches with order penalty
2. **Joined Words**: "newyork" ↔ "new york"
3. **Fuzzy Matching**: Edit distance ≤ 1 (typos)
4. **Prefix/Suffix**: "bat" matches "batman"
5. **LCS**: Fallback for substring matching

### Score Fusion

```csharp
final_score = MAX(relevancy_score, coverage_score)
```

Takes the best of both approaches for optimal results.

## Configuration

### Default Configuration
```csharp
var engine = SearchEngine.CreateDefault();
// - Index sizes: [2, 3] (dual n-grams)
// - Coverage: enabled (all algorithms)
// - Start padding: 2 characters
// - Stop term limit: 1,250,000
```

### Minimal Configuration (Relevancy Only)
```csharp
var engine = SearchEngine.CreateMinimal();
// - Index sizes: [3] (single n-gram)
// - Coverage: disabled
// - Faster but less accurate
```

### Custom Configuration
```csharp
var engine = new SearchEngine(
    indexSizes: new[] { 2, 3 },
    startPadSize: 2,
    stopPadSize: 0,
    enableCoverage: true,
    textNormalizer: TextNormalizer.CreateDefault(),
    tokenizerSetup: TokenizerSetup.CreateDefault(),
    coverageSetup: CoverageSetup.CreateDefault(),
    stopTermLimit: 1_250_000
);
```

## Algorithm Details

### TF-IDF Formula

```
IDF(term) = 1 + log₁₀(N × tf / df)
weight = IDF / ||document_vector||₂
byte_weight = round(weight × 255)
```

### Levenshtein Distance (Myers' Algorithm)

Bit-parallel algorithm with O(m) space and O(mn/w) time where w=64 (word size).

```csharp
VP = all 1s  // Vertical positive
VN = all 0s  // Vertical negative
for each character in text:
    update VP, VN using bit operations
    update score based on last bit
```

### Coverage Score

```
coverage_ratio = matched_characters / query_length
coverage_score = min(coverage_ratio × 255, 255)
```

## Testing

Run unit tests:
```bash
dotnet test
```

## License

This library is licensed under the [MIT](https://github.com/lofcz/Infidex/blob/master/LICENSE) license. 💜
