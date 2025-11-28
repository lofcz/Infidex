using Infidex.Core;
using Infidex.Metrics;
using Infidex.Tokenization;

namespace Infidex.Scoring;

/// <summary>
/// Pure functions for segment consolidation and best segment lookup.
/// </summary>
internal static class SegmentProcessor
{
    /// <summary>
    /// Consolidates segment scores to return only the best-scoring segment per DocumentKey.
    /// </summary>
    public static ScoreEntry[] ConsolidateSegments(IEnumerable<ScoreEntry> scores, Dictionary<int, byte>? bestSegmentsMap)
    {
        Dictionary<long, ScoreEntry> bestByKey = new Dictionary<long, ScoreEntry>();

        foreach (ScoreEntry entry in scores)
        {
            if (!bestByKey.TryGetValue(entry.DocumentId, out ScoreEntry existing))
            {
                bestByKey[entry.DocumentId] = entry;
            }
            else
            {
                if (entry.CompareTo(existing) > 0)
                {
                    bestByKey[entry.DocumentId] = entry;
                }
            }
        }

        ScoreEntry[] result = bestByKey.Values.ToArray();
        Array.Sort(result, (a, b) => b.CompareTo(a)); // Descending sort
        return result;
    }

    /// <summary>
    /// Returns the best segment text for a document using the bestSegments map.
    /// </summary>
    public static string GetBestSegmentText(
        Document doc,
        Dictionary<int, byte>? bestSegmentsMap,
        DocumentCollection documents,
        TextNormalizer? textNormalizer)
    {
        string docText = doc.IndexedText;

        if (bestSegmentsMap != null && bestSegmentsMap.Count > 0)
        {
            List<Document> allSegments = documents.GetDocumentsForPublicKey(doc.DocumentKey);
            if (allSegments.Count > 0)
            {
                Document firstSeg = allSegments[0];
                int baseId = firstSeg.Id - firstSeg.SegmentNumber;

                if (bestSegmentsMap.TryGetValue(baseId, out byte bestSegmentNum))
                {
                    Document? bestSegmentDoc = documents.GetDocumentOfSegment(doc.DocumentKey, bestSegmentNum);
                    if (bestSegmentDoc != null)
                    {
                        docText = bestSegmentDoc.IndexedText;
                    }
                }
            }
        }

        if (textNormalizer != null)
        {
            docText = textNormalizer.Normalize(docText);
        }

        return docText;
    }

    /// <summary>
    /// Calculates LCS sum for coverage.
    /// </summary>
    public static int CalculateLcs(string q, string r, int errorTolerance)
    {
        string qNorm = q.ToLowerInvariant();
        string rNorm = r.ToLowerInvariant();
        return StringMetrics.Lcs(qNorm, rNorm, errorTolerance);
    }
}

