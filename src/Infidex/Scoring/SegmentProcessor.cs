using Infidex.Core;
using Infidex.Internalized.CommunityToolkit;
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
    public static ScoreArray ConsolidateSegments(ScoreArray scores, Dictionary<int, byte>? bestSegmentsMap)
    {
        ScoreArray consolidated = new ScoreArray();
        Dictionary<long, (ushort Score, byte Tiebreaker)> scoresByKey = new Dictionary<long, (ushort Score, byte Tiebreaker)>();

        foreach (ScoreEntry entry in scores.GetAll())
        {
            long docKey = entry.DocumentId;

            if (!scoresByKey.TryGetValue(docKey, out (ushort Score, byte Tiebreaker) existing))
            {
                scoresByKey[docKey] = (entry.Score, entry.Tiebreaker);
            }
            else
            {
                if (entry.Score > existing.Score ||
                    (entry.Score == existing.Score && entry.Tiebreaker > existing.Tiebreaker))
                {
                    scoresByKey[docKey] = (entry.Score, entry.Tiebreaker);
                }
            }
        }

        foreach (KeyValuePair<long, (ushort Score, byte Tiebreaker)> kvp in scoresByKey)
        {
            consolidated.Add(kvp.Key, kvp.Value.Score, kvp.Value.Tiebreaker);
        }

        return consolidated;
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

