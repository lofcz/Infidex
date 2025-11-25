using Microsoft.VisualStudio.TestTools.UnitTesting;
using Infidex.Core;
using Infidex.Api;
using System.Linq;

namespace Infidex.Tests;

[TestClass]
public class SegmentTrackingTests
{
    // Helper method to create documents with the new multi-field API
    private static Document CreateDoc(long documentKey, int segmentNumber, string text, string clientInfo)
    {
        var fields = new DocumentFields();
        fields.AddField("content", text, Weight.Med, indexable: true);
        var doc = new Document(documentKey, segmentNumber, fields, clientInfo);
        
        // Set IndexedText for tests that check it directly (normally set by VectorModel.IndexDocument)
        doc.GetType().GetProperty("IndexedText")!.SetValue(doc, text);
        
        return doc;
    }
    
    [TestMethod]
    public void DocumentCollection_MultipleSegments_StoresCorrectly()
    {
        var collection = new DocumentCollection();
        
        // Add three segments of the same document
        var seg0 = collection.AddDocument(CreateDoc(100L, 0, "Segment zero text", ""));
        var seg1 = collection.AddDocument(CreateDoc(100L, 1, "Segment one text", ""));
        var seg2 = collection.AddDocument(CreateDoc(100L, 2, "Segment two text", ""));
        
        // Verify they have consecutive internal IDs
        Assert.AreEqual(0, seg0.Id);
        Assert.AreEqual(1, seg1.Id);
        Assert.AreEqual(2, seg2.Id);
        
        // Verify the formula: baseId = internalId - segmentNumber
        Assert.AreEqual(0, seg0.Id - seg0.SegmentNumber); // 0 - 0 = 0
        Assert.AreEqual(0, seg1.Id - seg1.SegmentNumber); // 1 - 1 = 0
        Assert.AreEqual(0, seg2.Id - seg2.SegmentNumber); // 2 - 2 = 0
    }
    
    [TestMethod]
    public void DocumentCollection_GetDocumentsForPublicKey_ReturnsAllSegments()
    {
        var collection = new DocumentCollection();
        
        collection.AddDocument(CreateDoc(100L, 0, "Seg 0", ""));
        collection.AddDocument(CreateDoc(100L, 1, "Seg 1", ""));
        collection.AddDocument(CreateDoc(100L, 2, "Seg 2", ""));
        collection.AddDocument(CreateDoc(200L, 0, "Different doc", ""));
        
        var segments = collection.GetDocumentsForPublicKey(100L);
        
        Assert.AreEqual(3, segments.Count);
        Assert.AreEqual(0, segments[0].SegmentNumber);
        Assert.AreEqual(1, segments[1].SegmentNumber);
        Assert.AreEqual(2, segments[2].SegmentNumber);
    }
    
    [TestMethod]
    public void DocumentCollection_GetDocumentOfSegment_ReturnsSpecificSegment()
    {
        var collection = new DocumentCollection();
        
        collection.AddDocument(CreateDoc(100L, 0, "Seg 0", ""));
        collection.AddDocument(CreateDoc(100L, 1, "Seg 1", ""));
        collection.AddDocument(CreateDoc(100L, 2, "Seg 2", ""));
        
        var seg1 = collection.GetDocumentOfSegment(100L, 1);
        
        Assert.IsNotNull(seg1);
        Assert.AreEqual(1, seg1.SegmentNumber);
        Assert.AreEqual("Seg 1", seg1.IndexedText);
    }
    
    [TestMethod]
    public void DocumentCollection_GetDocumentOfSegment_NonExistent_ReturnsNull()
    {
        var collection = new DocumentCollection();
        
        collection.AddDocument(CreateDoc(100L, 0, "Seg 0", ""));
        
        var seg5 = collection.GetDocumentOfSegment(100L, 5);
        
        Assert.IsNull(seg5);
    }
    
    [TestMethod]
    public void Search_SegmentedDocument_ReturnsBestSegment()
    {
        var engine = SearchEngine.CreateDefault();
        
        // Add segmented document - segment 1 contains "fox"
        var segments = new[]
        {
            CreateDoc(1L, 0, "Introduction to the topic of animals", ""),
            CreateDoc(1L, 1, "The quick brown fox jumps over the lazy dog", ""),
            CreateDoc(1L, 2, "Conclusion and summary of findings", "")
        };
        
        engine.IndexDocuments(segments);
        
        // Search for "fox" - should find segment 1 as the best match
        var result = engine.Search(new Query("fox", 10));
        
        // Should return only one result per DocumentKey
        Assert.AreEqual(1, result.Records.Length);
        Assert.AreEqual(1L, result.Records[0].DocumentId);
        
        // The score should reflect the best-matching segment
        Assert.IsTrue(result.Records[0].Score > 0);
    }
    
    [TestMethod]
    public void Search_MultipleSegmentedDocuments_ConsolidatesCorrectly()
    {
        var engine = SearchEngine.CreateDefault();
        
        // Index all documents in one call
        var allDocs = new[]
        {
            // Document 1: Three segments, "batman" in segment 1
            CreateDoc(1L, 0, "Introduction chapter one", ""),
            CreateDoc(1L, 1, "Batman fights crime in Gotham City", ""),
            CreateDoc(1L, 2, "Conclusion chapter one", ""),
            
            // Document 2: Two segments, "batman" in segment 0
            CreateDoc(2L, 0, "Batman and Robin save the day", ""),
            CreateDoc(2L, 1, "The end of their adventure", ""),
            
            // Document 3: No segments, just regular document  
            CreateDoc(3L, 0, "Superman flies faster than a speeding bullet", "")
        };
        
        engine.IndexDocuments(allDocs);
        
        var result = engine.Search(new Query("batman", 10));
        
        // Should return 2 results (docs 1 and 2), not 6 (total segments including doc 3)
        Assert.AreEqual(2, result.Records.Length);
        
        // Both should be batman-containing documents
        Assert.IsTrue(result.Records.Any(r => r.DocumentId == 1L));
        Assert.IsTrue(result.Records.Any(r => r.DocumentId == 2L));
    }
    
    [TestMethod]
    public void Search_OnlyNonMatchingSegments_ReturnsNoResults()
    {
        var engine = SearchEngine.CreateDefault();
        
        engine.IndexDocuments(new[]
        {
            CreateDoc(1L, 0, "The cat sat on the mat", ""),
            CreateDoc(1L, 1, "The dog ran through the park", ""),
            CreateDoc(1L, 2, "The bird flew in the sky", "")
        });
        
        var result = engine.Search(new Query("batman", 10));
        
        // Should return no results
        Assert.AreEqual(0, result.Records.Length);
    }
    
    [TestMethod]
    public void Search_OnlyNonMatchingSegments_ReturnsNoResults2()
    {
        var engine = SearchEngine.CreateDefault();
        
        engine.IndexDocuments(new[]
        {
            CreateDoc(1L, 0, "The cat sat on the mat", ""),
            CreateDoc(2L, 0, "The dog ran through the park", ""),
            CreateDoc(3L, 0, "The bird flew in the sky", "")
        });
        
        var result = engine.Search(new Query("batman", 10));
        
        // Should return no results
        Assert.AreEqual(0, result.Records.Length);
    }
    
    [TestMethod]
    public void Search_MixedSegmentedAndNonSegmented_HandlesCorrectly()
    {
        var engine = SearchEngine.CreateDefault();
        
        // Index all documents in one call
        var allDocs = new[]
        {
            // Segmented document
            CreateDoc(1L, 0, "Chapter 1 introduction", ""),
            CreateDoc(1L, 1, "The hero begins his journey", ""),
            
            // Non-segmented documents
            CreateDoc(2L, 0, "The hero saves the day", ""),
            CreateDoc(3L, 0, "A story about courage", "")
        };
        
        engine.IndexDocuments(allDocs);
        
        var result = engine.Search(new Query("hero", 10));
        
        // Should return 2 results: doc 1 (best segment) and doc 2
        Assert.AreEqual(2, result.Records.Length);
        Assert.IsTrue(result.Records.Any(r => r.DocumentId == 1L));
        Assert.IsTrue(result.Records.Any(r => r.DocumentId == 2L));
    }
    
    [TestMethod]
    public void DeletedSegments_ExcludedFromResults()
    {
        var collection = new DocumentCollection();
        
        // Add segmented document
        collection.AddDocument(CreateDoc(1L, 0, "Segment 0 with batman", ""));
        collection.AddDocument(CreateDoc(1L, 1, "Segment 1 with batman", ""));
        collection.AddDocument(CreateDoc(1L, 2, "Segment 2 with batman", ""));
        
        // Verify segments exist
        var allSegments = collection.GetDocumentsForPublicKey(1L);
        Assert.AreEqual(3, allSegments.Count);
        
        // Mark entire document as deleted
        collection.DeleteDocumentsByKey(1L);
        
        // Verify segments are marked as deleted
        var segments = collection.GetDocumentsForPublicKey(1L);
        foreach (var seg in segments)
        {
            Assert.IsTrue(seg.Deleted);
        }
    }
    
    [TestMethod]
    public void RemoveDeletedDocuments_CompactsCollectionAndLookups()
    {
        var collection = new DocumentCollection();
        
        // Two keys, first one will be deleted
        var d1 = collection.AddDocument(CreateDoc(1L, 0, "Doc 1", ""));
        var d2 = collection.AddDocument(CreateDoc(2L, 0, "Doc 2", ""));
        var d3 = collection.AddDocument(CreateDoc(3L, 0, "Doc 3", ""));
        
        // Mark key 2 as deleted
        collection.DeleteDocumentsByKey(2L);
        
        // Physically remove deleted docs and compact IDs / lookups
        collection.RemoveDeletedDocuments();
        
        // Only keys 1 and 3 should remain
        var all = collection.GetAllDocuments();
        Assert.AreEqual(2, all.Count);
        CollectionAssert.AreEquivalent(
            new long[] { 1L, 3L },
            all.Select(d => d.DocumentKey).ToArray());
        
        // Remaining documents should have dense, zero-based Ids
        Assert.AreEqual(0, all[0].Id);
        Assert.AreEqual(1, all[1].Id);
        
        // Lookups should reflect the new state
        Assert.AreEqual(0, collection.GetDocumentsByKey(2L).Count); // deleted key
        Assert.AreEqual(1, collection.GetDocumentsByKey(1L).Count);
        Assert.AreEqual(1, collection.GetDocumentsByKey(3L).Count);
    }
    
    [TestMethod]
    public void RemoveDeletedDocuments_CompactsSegmentedDocumentIds()
    {
        var collection = new DocumentCollection();
        
        // Segmented document for key 1
        collection.AddDocument(CreateDoc(1L, 0, "Seg 0", ""));
        collection.AddDocument(CreateDoc(1L, 1, "Seg 1", ""));
        collection.AddDocument(CreateDoc(1L, 2, "Seg 2", ""));
        
        // Non-segmented document for key 2
        collection.AddDocument(CreateDoc(2L, 0, "Other doc", ""));
        
        // Delete the segmented document
        collection.DeleteDocumentsByKey(1L);
        collection.RemoveDeletedDocuments();
        
        // Only key 2 should remain
        var remaining = collection.GetAllDocuments();
        Assert.AreEqual(1, remaining.Count);
        Assert.AreEqual(2L, remaining[0].DocumentKey);
        Assert.AreEqual(0, remaining[0].Id); // compacted to first position
        
        // Segment lookups for key 1 should be empty
        Assert.AreEqual(0, collection.GetDocumentsForPublicKey(1L).Count);
        Assert.IsNull(collection.GetDocumentOfSegment(1L, 0));
    }
    
    [TestMethod]
    public void SegmentContinuation_TokenizerSkipsStartPadding()
    {
        var tokenizer = new Tokenization.Tokenizer(
            indexSizes: new[] { 2, 3 },
            startPadSize: 2,
            stopPadSize: 0);
        
        // First segment (not continuation) - should have start padding
        var seg0Shingles = tokenizer.TokenizeForIndexing("test", isSegmentContinuation: false);
        
        // Continuation segment - should skip start padding
        var seg1Shingles = tokenizer.TokenizeForIndexing("test", isSegmentContinuation: true);
        
        // Continuation should have fewer shingles (no start padding)
        Assert.IsTrue(seg0Shingles.Count >= seg1Shingles.Count);
        
        // The first shingles should be different (due to padding difference)
        if (seg0Shingles.Count > 0 && seg1Shingles.Count > 0)
        {
            Assert.AreNotEqual(seg0Shingles[0].Text, seg1Shingles[0].Text);
        }
    }
    
    [TestMethod]
    public void LargeNumberOfSegments_HandlesEfficiently()
    {
        var engine = SearchEngine.CreateDefault();
        
        // Create a document with 10 segments
        var segments = new Document[10];
        for (int i = 0; i < 10; i++)
        {
            segments[i] = CreateDoc(1L, i, $"Segment {i} text content", $"metadata {i}");
        }
        
        // Add one segment with the search term
        segments[5] = CreateDoc(1L, 5, "This segment contains batman", "metadata 5");
        
        engine.IndexDocuments(segments);
        
        var result = engine.Search(new Query("batman", 10));
        
        // Should return exactly 1 result (not 10)
        Assert.AreEqual(1, result.Records.Length);
        Assert.AreEqual(1L, result.Records[0].DocumentId);
    }
}

