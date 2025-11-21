using Microsoft.VisualStudio.TestTools.UnitTesting;
using Infidex;
using Infidex.Core;
using Infidex.Api;
using Infidex.Coverage;

namespace Infidex.Tests;

[TestClass]
public class QueryTests
{
    [TestMethod]
    public void Query_DefaultConstructor_SetsDefaults()
    {
        var query = new Query();
        
        Assert.AreEqual(string.Empty, query.Text);
        Assert.AreEqual(10, query.MaxNumberOfRecordsToReturn);
        Assert.IsTrue(query.EnableCoverage);
        Assert.IsFalse(query.EnableFacets);
        Assert.IsFalse(query.EnableBoost);
        Assert.AreEqual(500, query.CoverageDepth);
        Assert.IsTrue(query.RemoveDuplicates);
        Assert.AreEqual(1000, query.TimeOutLimitMilliseconds);
    }
    
    [TestMethod]
    public void Query_WithTextAndMaxResults_SetsProperties()
    {
        var query = new Query("test query", 20);
        
        Assert.AreEqual("test query", query.Text);
        Assert.AreEqual(20, query.MaxNumberOfRecordsToReturn);
        Assert.IsTrue(query.EnableCoverage);
        Assert.IsTrue(query.RemoveDuplicates);
    }
    
    [TestMethod]
    public void Query_CopyConstructor_CopiesAllProperties()
    {
        var original = new Query("test", 15)
        {
            EnableFacets = true,
            EnableBoost = true,
            CoverageDepth = 200,
            RemoveDuplicates = false,
            TimeOutLimitMilliseconds = 2000,
            LogPrefix = "TEST"
        };
        
        var copy = new Query(original);
        
        Assert.AreEqual(original.Text, copy.Text);
        Assert.AreEqual(original.MaxNumberOfRecordsToReturn, copy.MaxNumberOfRecordsToReturn);
        Assert.AreEqual(original.EnableFacets, copy.EnableFacets);
        Assert.AreEqual(original.EnableBoost, copy.EnableBoost);
        Assert.AreEqual(original.CoverageDepth, copy.CoverageDepth);
        Assert.AreEqual(original.RemoveDuplicates, copy.RemoveDuplicates);
        Assert.AreEqual(original.TimeOutLimitMilliseconds, copy.TimeOutLimitMilliseconds);
        Assert.AreEqual(original.LogPrefix, copy.LogPrefix);
    }
    
    [TestMethod]
    public void Query_CopyConstructor_DeepCopiesCoverageSetup()
    {
        var coverageSetup = new CoverageSetup
        {
            MinWordSize = 3,
            LevenshteinMaxWordSize = 15,
            CoverageMinWordHitsAbs = 2,
            CoverWholeQuery = false,
            CoverFuzzyWords = false,
            CoverageDepth = 300
        };
        
        var original = new Query("test", 10)
        {
            CoverageSetup = coverageSetup
        };
        
        var copy = new Query(original);
        
        // Verify copy has its own CoverageSetup instance
        Assert.IsNotNull(copy.CoverageSetup);
        Assert.AreNotSame(original.CoverageSetup, copy.CoverageSetup);
        
        // Verify all properties are copied
        Assert.AreEqual(original.CoverageSetup.MinWordSize, copy.CoverageSetup.MinWordSize);
        Assert.AreEqual(original.CoverageSetup.LevenshteinMaxWordSize, copy.CoverageSetup.LevenshteinMaxWordSize);
        Assert.AreEqual(original.CoverageSetup.CoverageMinWordHitsAbs, copy.CoverageSetup.CoverageMinWordHitsAbs);
        Assert.AreEqual(original.CoverageSetup.CoverWholeQuery, copy.CoverageSetup.CoverWholeQuery);
        Assert.AreEqual(original.CoverageSetup.CoverFuzzyWords, copy.CoverageSetup.CoverFuzzyWords);
        Assert.AreEqual(original.CoverageSetup.CoverageDepth, copy.CoverageSetup.CoverageDepth);
        
        // Modify original and verify copy is not affected
        original.CoverageSetup.MinWordSize = 99;
        original.CoverageSetup.CoverWholeQuery = true;
        
        Assert.AreEqual(3, copy.CoverageSetup.MinWordSize, "Copy should be independent of original");
        Assert.IsFalse(copy.CoverageSetup.CoverWholeQuery, "Copy should be independent of original");
    }
    
    [TestMethod]
    public void Query_CopyConstructor_HandlesNullCoverageSetup()
    {
        var original = new Query("test", 10)
        {
            CoverageSetup = null
        };
        
        var copy = new Query(original);
        
        Assert.IsNull(copy.CoverageSetup);
    }
    
    [TestMethod]
    public void Document_Constructor_WithSegmentNumber_CreatesSegmentedDocument()
    {
        var doc = new Document(123L, 5, "Test content for segment 5");
        
        Assert.AreEqual(123L, doc.DocumentKey);
        Assert.AreEqual(5, doc.SegmentNumber);
        Assert.IsNotNull(doc.Fields);
        Assert.AreEqual(1, doc.Fields.GetSearchAbleFieldList().Count);
        Assert.AreEqual("Test content for segment 5", doc.Fields.GetSearchAbleFieldList()[0].Value);
    }
    
    [TestMethod]
    public void SearchEngine_QuerySearch_ReturnsResult()
    {
        var engine = SearchEngine.CreateDefault();
        
        var docs = new[]
        {
            new Document(1L, "The quick brown fox"),
            new Document(2L, "The lazy dog"),
            new Document(3L, "Quick thinking")
        };
        
        engine.IndexDocuments(docs);
        
        var query = new Query("quick", 10);
        var result = engine.Search(query);
        
        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Records);
        Assert.IsTrue(result.Records.Length > 0);
    }
    
    [TestMethod]
    public void SearchEngine_QueryWithMaxResults_LimitsResults_IdenticalDocuments()
    {
        var engine = SearchEngine.CreateDefault();
        
        // Create 20 documents with identical content (like reference test)
        var docs = new Document[20];
        for (int i = 0; i < 20; i++)
        {
            docs[i] = new Document(i, "batman saves the day");
        }
        
        engine.IndexDocuments(docs);
        
        var query = new Query("batman", 5);
        var result = engine.Search(query);
        
        // Should return EXACTLY 5 results
        Assert.AreEqual(5, result.Records.Length, $"Expected exactly 5 results but got {result.Records.Length}");
    }
    
    [TestMethod]
    public void SearchEngine_QueryWithMaxResults_LimitsResults_VariedDocuments()
    {
        var engine = SearchEngine.CreateDefault();
        
        // Create 20 documents with slight variations (like reference test 2)
        var docs = new Document[20];
        for (int i = 0; i < 20; i++)
        {
            docs[i] = new Document(i, $"batman saves the day story {i}");
        }
        
        engine.IndexDocuments(docs);
        
        var query = new Query("batman", 8);
        var result = engine.Search(query);
        
        Assert.AreEqual(8, result.Records.Length, $"Expected exactly 8 results but got {result.Records.Length}");
    }
    
    [TestMethod]
    public void SearchEngine_QueryWithMaxResults_LimitsResults_DifferentDocuments()
    {
        var engine = SearchEngine.CreateDefault();
        
        // Create 20 completely different documents all containing "batman" (like reference test 3)
        var docs = new Document[20];
        docs[0] = new Document(0, "Batman is a superhero appearing in American comic books published by DC Comics.");
        docs[1] = new Document(1, "The character was created by Bob Kane and Bill Finger, and first appeared in Detective Comics #27.");
        docs[2] = new Document(2, "Batman's secret identity is Bruce Wayne, a wealthy American playboy, philanthropist, and industrialist.");
        docs[3] = new Document(3, "He resides in Gotham City and operates out of the Batcave.");
        docs[4] = new Document(4, "His archenemy is the Joker, a criminal mastermind with a clown-like appearance.");
        docs[5] = new Document(5, "Other notable villains include Penguin, Riddler, Catwoman, and Two-Face.");
        docs[6] = new Document(6, "Batman comic books by DC Comics are very popular.");
        docs[7] = new Document(7, "Batman Arkham games are popular among gamers.");
        docs[8] = new Document(8, "The Dark Knight is a critically acclaimed Batman movie.");
        docs[9] = new Document(9, "Christian Bale played Batman in Christopher Nolan's trilogy.");
        docs[10] = new Document(10, "Batman drives the Batmobile through city streets.");
        docs[11] = new Document(11, "Batman has many enemies like Joker and Harley Quinn.");
        docs[12] = new Document(12, "Robin is Batman's sidekick.");
        docs[13] = new Document(13, "Alfred Pennyworth is Batman's loyal butler.");
        docs[14] = new Document(14, "Commissioner Gordon often works with Batman.");
        docs[15] = new Document(15, "The Justice League includes Batman, Superman, and Wonder Woman.");
        docs[16] = new Document(16, "Batman uses various gadgets and martial arts.");
        docs[17] = new Document(17, "Batman animated series is beloved by many fans.");
        docs[18] = new Document(18, "Zack Snyder directed Batman v Superman.");
        docs[19] = new Document(19, "Robert Pattinson is the latest actor to portray Batman.");
        
        engine.IndexDocuments(docs);
        
        var query = new Query("batman", 12);
        var result = engine.Search(query);
        
        Assert.AreEqual(12, result.Records.Length, $"Expected exactly 12 results but got {result.Records.Length}");
    }
    
    [TestMethod]
    public void SearchEngine_ExactMatch_RanksAtTop()
    {
        var engine = SearchEngine.CreateDefault();
        
        // Create 20 documents with varying relevance to "dark knight rises"
        var docs = new Document[20];
        docs[0] = new Document(0, "Batman is a superhero appearing in American comic books.");
        docs[1] = new Document(1, "The character was created by Bob Kane and Bill Finger.");
        docs[2] = new Document(2, "Bruce Wayne is Batman's secret identity.");
        docs[3] = new Document(3, "He operates out of the Batcave in Gotham City.");
        docs[4] = new Document(4, "The Joker is Batman's archenemy and nemesis.");
        docs[5] = new Document(5, "The Dark Knight Rises"); // Exact match
        docs[6] = new Document(6, "Other villains include Penguin and Riddler.");
        docs[7] = new Document(7, "Batman comic books are published by DC Comics.");
        docs[8] = new Document(8, "The Dark Knight Rises is an epic conclusion"); // Near-exact match with extra words
        docs[9] = new Document(9, "Batman uses gadgets and martial arts skills.");
        docs[10] = new Document(10, "Christian Bale portrayed Batman in the trilogy.");
        docs[11] = new Document(11, "The Dark Knight was a critically acclaimed film."); // Contains some query terms
        docs[12] = new Document(12, "Robin is Batman's trusted sidekick and partner.");
        docs[13] = new Document(13, "Alfred Pennyworth is Batman's loyal butler.");
        docs[14] = new Document(14, "Commissioner Gordon works with Batman regularly.");
        docs[15] = new Document(15, "The Justice League includes Batman and Superman.");
        docs[16] = new Document(16, "Batman animated series is beloved by fans.");
        docs[17] = new Document(17, "Zack Snyder directed Batman v Superman movie.");
        docs[18] = new Document(18, "Robert Pattinson is the latest Batman actor.");
        docs[19] = new Document(19, "The Batmobile is Batman's iconic vehicle.");
        
        engine.IndexDocuments(docs);
        
        var query = new Query("dark knight rises", 10);
        var result = engine.Search(query);
        
        Assert.IsTrue(result.Records.Length > 0, "Should return results");
        
        // Exact match (doc 5) should be at the top
        Assert.AreEqual(5, result.Records[0].DocumentId, 
            $"Expected exact match (doc 5) at position 0, but got doc {result.Records[0].DocumentId} with score {result.Records[0].Score}");
        
        // Near-exact match (doc 8) should be in top 3
        var topThreeIds = result.Records.Take(3).Select(r => r.DocumentId).ToArray();
        Assert.IsTrue(topThreeIds.Contains(8), 
            $"Expected near-exact match (doc 8) in top 3, but top 3 are: [{string.Join(", ", topThreeIds)}]");
        
        // Verify scores are descending
        for (int i = 1; i < result.Records.Length; i++)
        {
            Assert.IsTrue(result.Records[i - 1].Score >= result.Records[i].Score,
                $"Scores should be descending: pos {i-1} score={result.Records[i - 1].Score}, pos {i} score={result.Records[i].Score}");
        }
    }
}

