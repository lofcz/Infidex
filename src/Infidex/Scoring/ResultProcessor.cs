using Infidex.Api;
using Infidex.Core;
using Infidex.Coverage;
using Infidex.Filtering;
using Infidex.Internalized.CommunityToolkit;
using System.Collections.Concurrent;

namespace Infidex.Scoring;

/// <summary>
/// Post-processing operations for search results: filtering, boosting, sorting, truncation.
/// </summary>
internal sealed class ResultProcessor
{
    private readonly DocumentCollection _documents;
    private readonly ThreadLocal<FilterCompiler> _filterCompiler;
    private readonly ThreadLocal<FilterVM> _filterVM;
    private readonly ConcurrentDictionary<Filter, CompiledFilter> _compiledFilterCache;

    public ResultProcessor(
        DocumentCollection documents,
        ThreadLocal<FilterCompiler> filterCompiler,
        ThreadLocal<FilterVM> filterVM,
        ConcurrentDictionary<Filter, CompiledFilter> compiledFilterCache)
    {
        _documents = documents;
        _filterCompiler = filterCompiler;
        _filterVM = filterVM;
        _compiledFilterCache = compiledFilterCache;
    }

    /// <summary>
    /// Applies filter to search results using bytecode VM.
    /// </summary>
    public ScoreEntry[] ApplyFilter(ScoreEntry[] results, Filter filter)
    {
        CompiledFilter compiled = _compiledFilterCache.GetOrAdd(filter, f => _filterCompiler.Value!.Compile(f));

        if (filter.NumberOfDocumentsInFilter == 0)
        {
            int matchCount = 0;
            IReadOnlyList<Document> allDocuments = _documents.GetAllDocuments();

            foreach (Document doc in allDocuments)
            {
                if (_filterVM.Value!.Execute(compiled, doc.Fields))
                {
                    matchCount++;
                }
            }

            filter.NumberOfDocumentsInFilter = matchCount;
        }

        List<ScoreEntry> filteredResults = [];

        foreach (ScoreEntry result in results)
        {
            Document? doc = _documents.GetDocumentByPublicKey(result.DocumentId);
            if (doc == null)
                continue;

            if (_filterVM.Value!.Execute(compiled, doc.Fields))
            {
                filteredResults.Add(result);
            }
        }

        return filteredResults.ToArray();
    }

    /// <summary>
    /// Applies boosts to search results.
    /// </summary>
    public ScoreEntry[] ApplyBoosts(ScoreEntry[] results, Boost[] boosts)
    {
        if (boosts == null || boosts.Length == 0)
            return results;

        List<(CompiledFilter compiled, int strength)> compiledBoosts = [];

        foreach (Boost boost in boosts)
        {
            if (boost.Filter == null)
                continue;

            CompiledFilter compiled = _compiledFilterCache.GetOrAdd(boost.Filter, f => _filterCompiler.Value!.Compile(f));
            compiledBoosts.Add((compiled, (int)boost.BoostStrength));
        }

        if (compiledBoosts.Count == 0)
            return results;

        for (int i = 0; i < results.Length; i++)
        {
            ScoreEntry result = results[i];
            Document? doc = _documents.GetDocumentByPublicKey(result.DocumentId);

            if (doc == null)
                continue;

            int totalBoost = 0;

            foreach ((CompiledFilter compiled, int strength) in compiledBoosts)
            {
                if (_filterVM.Value!.Execute(compiled, doc.Fields))
                {
                    totalBoost += strength;
                }
            }

            if (totalBoost > 0)
            {
                float newScore = result.Score + totalBoost;
                results[i] = new ScoreEntry(newScore, result.DocumentId, result.Tiebreaker, result.SegmentNumber);
            }
        }

        Array.Sort(results, (a, b) => b.Score.CompareTo(a.Score));
        return results;
    }

    /// <summary>
    /// Applies sorting to search results.
    /// </summary>
    public ScoreEntry[] ApplySort(ScoreEntry[] results, Field sortByField, bool ascending)
    {
        (ScoreEntry Entry, object? SortValue)[] withSortKeys = results.Select(r =>
        {
            Document? doc = _documents.GetDocumentByPublicKey(r.DocumentId);
            Field? field = doc?.Fields.GetField(sortByField.Name);
            return (Entry: r, SortValue: field?.Value);
        }).ToArray();

        if (ascending)
            Array.Sort(withSortKeys, (a, b) => CompareValues(a.SortValue, b.SortValue));
        else
            Array.Sort(withSortKeys, (a, b) => CompareValues(b.SortValue, a.SortValue));

        return withSortKeys.Select(x => x.Entry).ToArray();
    }

    /// <summary>
    /// Calculates where to truncate results based on word hits and scores.
    /// </summary>
    public static int CalculateTruncationIndex(
        ScoreEntry[] results,
        int maxWordHits,
        Span2D<byte> lcsAndWordHitsSpan,
        Dictionary<long, int> documentKeyToIndex,
        CoverageSetup coverageSetup)
    {
        if (results == null || results.Length == 0)
            return -1;

        int minWordHits = Math.Max(
            coverageSetup.CoverageMinWordHitsAbs,
            maxWordHits - coverageSetup.CoverageMinWordHitsRelative);

        for (int i = results.Length - 1; i >= 0; i--)
        {
            if (!documentKeyToIndex.TryGetValue(results[i].DocumentId, out int docIndex))
                continue;

            if (docIndex >= lcsAndWordHitsSpan.Width)
                continue;

            byte wordHitsByte = lcsAndWordHitsSpan[1, docIndex];
            byte lcsByte = lcsAndWordHitsSpan[0, docIndex];
            
            if (wordHitsByte >= minWordHits || lcsByte > 0 || results[i].Score >= coverageSetup.TruncationScore)
            {
                return i;
            }
        }

        return -1;
    }

    private static int CompareValues(object? a, object? b)
    {
        switch (a)
        {
            case null when b == null:
                return 0;
            case null:
                return -1;
        }

        if (b == null) return 1;

        if (a is IComparable ca && b is IComparable cb && a.GetType() == b.GetType())
        {
            return ca.CompareTo(cb);
        }

        return string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal);
    }
}
