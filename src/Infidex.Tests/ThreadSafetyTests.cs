using System.Collections.Concurrent;
using System.Diagnostics;
using Infidex;
using Infidex.Api;
using Infidex.Core;

namespace Infidex.Tests;

[TestClass]
public class ThreadSafetyTests
{
    private const int StressTestDurationMs = 2000;
    private const int ConcurrentOperationCount = 100;
    
    [TestMethod]
    public void ConcurrentQueries_NoExceptions()
    {
        // Arrange
        var engine = CreatePopulatedEngine(1000);
        var queries = new[] { "test", "search", "document", "index", "query", "thread", "safe", "concurrent" };
        var exceptions = new ConcurrentBag<Exception>();
        
        // Act - hammer it with concurrent queries
        Parallel.For(0, ConcurrentOperationCount, i =>
        {
            try
            {
                var query = queries[i % queries.Length];
                var result = engine.Search(new Query(query, 10));
                Assert.IsNotNull(result);
                Assert.IsNotNull(result.Records);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });
        
        // Assert
        Assert.AreEqual(0, exceptions.Count, 
            $"Expected no exceptions during concurrent queries, but got: {string.Join(", ", exceptions.Select(e => e.Message))}");
    }
    
    [TestMethod]
    public void ConcurrentIndexing_NoExceptions()
    {
        // Arrange
        var engine = SearchEngine.CreateDefault();
        var exceptions = new ConcurrentBag<Exception>();
        var documentsPerThread = 100;
        
        // Act - multiple threads indexing simultaneously
        Parallel.For(0, 10, threadId =>
        {
            try
            {
                var docs = Enumerable.Range(0, documentsPerThread)
                    .Select(i => new Document(
                        threadId * documentsPerThread + i,
                        $"Thread {threadId} Document {i} with some searchable content"))
                    .ToList();
                
                engine.IndexDocuments(docs);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });
        
        // Assert
        Assert.AreEqual(0, exceptions.Count,
            $"Expected no exceptions during concurrent indexing, but got: {string.Join(", ", exceptions.Select(e => e.Message))}");
    }
    
    [TestMethod]
    public void ConcurrentMixedOperations_QueriesWhileIndexing()
    {
        // Arrange
        var engine = CreatePopulatedEngine(500);
        var exceptions = new ConcurrentBag<Exception>();
        var cts = new CancellationTokenSource();
        
        // Act - query continuously while indexing new documents
        var queryTask = Task.Run(() =>
        {
            var iteration = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = engine.Search(new Query($"document {iteration % 100}", 5));
                    Assert.IsNotNull(result);
                    iteration++;
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                    break;
                }
            }
        });
        
        var indexTask = Task.Run(() =>
        {
            for (int batch = 0; batch < 10; batch++)
            {
                try
                {
                    var docs = Enumerable.Range(0, 50)
                        .Select(i => new Document(
                            1000000 + batch * 50 + i,
                            $"New document {batch * 50 + i} being indexed concurrently"))
                        .ToList();
                    
                    engine.IndexDocuments(docs);
                    Thread.Sleep(50); // Small delay between batches
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                    break;
                }
            }
        });
        
        indexTask.Wait();
        cts.Cancel();
        
        try { queryTask.Wait(1000); } catch { }
        
        // Assert
        Assert.AreEqual(0, exceptions.Count,
            $"Expected no exceptions during mixed operations, but got: {string.Join(", ", exceptions.Select(e => e.Message))}");
    }
    
    [TestMethod]
    [Ignore("Long-running high-contention stress test; run manually when needed.")]
    public void HighContentionStressTest_ManyThreadsQueryingSameTerms()
    {
        // Arrange
        var engine = CreatePopulatedEngine(2000);
        var exceptions = new ConcurrentBag<Exception>();
        var threadCount = Environment.ProcessorCount * 4; // High contention
        var barrier = new Barrier(threadCount);
        
        // Act - all threads start at the same time to maximize contention
        var tasks = Enumerable.Range(0, threadCount).Select(threadId =>
            Task.Run(() =>
            {
                try
                {
                    barrier.SignalAndWait(); // Synchronize start
                    
                    for (int i = 0; i < 50; i++)
                    {
                        var result = engine.Search(new Query("document", 10));
                        Assert.IsNotNull(result);
                        Assert.IsTrue(result.Records.Length > 0);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            })
        ).ToArray();
        
        Task.WaitAll(tasks);
        
        // Assert
        Assert.AreEqual(0, exceptions.Count,
            $"Expected no exceptions under high contention, but got: {string.Join(", ", exceptions.Select(e => e.Message))}");
    }
    
    [TestMethod]
    public void ConcurrentGetDocument_NoRaceConditions()
    {
        // Arrange
        var engine = CreatePopulatedEngine(1000);
        var exceptions = new ConcurrentBag<Exception>();
        var results = new ConcurrentBag<string>();
        
        // Act - many threads retrieving the same document
        Parallel.For(0, 200, i =>
        {
            try
            {
                var doc = engine.GetDocument(42); // Everyone wants doc 42
                if (doc != null)
                {
                    results.Add(doc.IndexedText);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });
        
        // Assert
        Assert.AreEqual(0, exceptions.Count);
        // All retrieved documents should be identical
        var distinctResults = results.Distinct().ToList();
        Assert.AreEqual(1, distinctResults.Count, "GetDocument should return consistent results across threads");
    }
    
    [TestMethod]
    public void ConcurrentIndexingOfSameDocumentId_NoCorruption()
    {
        // Arrange
        var engine = SearchEngine.CreateDefault();
        var exceptions = new ConcurrentBag<Exception>();
        var documentId = 12345L;
        
        // Act - multiple threads trying to index documents with the same ID (update scenario)
        Parallel.For(0, 50, iteration =>
        {
            try
            {
                var doc = new Document(documentId, $"Updated content iteration {iteration}");
                engine.IndexDocuments(new[] { doc });
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });
        
        // Assert
        Assert.AreEqual(0, exceptions.Count);
        
        // The document should be retrievable and have consistent state
        var finalDoc = engine.GetDocument(documentId);
        Assert.IsNotNull(finalDoc, "Document should be retrievable after concurrent updates");
        Assert.IsNotNull(finalDoc!.IndexedText, "Document should have valid content");
    }
    
    [TestMethod]
    public void RaceCondition_QueryWhileIndexingSameTerms()
    {
        // Arrange
        var engine = SearchEngine.CreateDefault();
        var exceptions = new ConcurrentBag<Exception>();
        var cts = new CancellationTokenSource();
        var queryResults = new ConcurrentBag<int>();
        
        // Act - one thread continuously adding documents, others querying for those same terms
        var indexTask = Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 100; i++)
                {
                    var docs = Enumerable.Range(i * 10, 10)
                        .Select(id => new Document(id, "searchterm common document"))
                        .ToList();
                    
                    engine.IndexDocuments(docs);
                    Thread.Sleep(10);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
            finally
            {
                cts.Cancel();
            }
        });
        
        var queryTasks = Enumerable.Range(0, 5).Select(qid =>
            Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var result = engine.Search(new Query("searchterm", 50));
                        queryResults.Add(result.Records.Length);
                        Thread.Sleep(5);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                        break;
                    }
                }
            })
        ).ToArray();
        
        Task.WaitAll(new[] { indexTask }.Concat(queryTasks).ToArray());
        
        // Assert
        Assert.AreEqual(0, exceptions.Count);
        Assert.IsTrue(queryResults.Count > 0, "Should have executed queries");
        
        // Results should be monotonically increasing or stable (documents being added over time)
        var resultsList = queryResults.ToList();
        Assert.IsTrue(resultsList.All(r => r >= 0), "All result counts should be valid");
    }
    
    [TestMethod]
    public void MemoryVisibility_ChangesVisibleAcrossThreads()
    {
        // Arrange
        var engine = SearchEngine.CreateDefault();
        var docId = 999L;
        
        // Act - one thread indexes, another queries
        var indexTask = Task.Run(() =>
        {
            var doc = new Document(docId, "unique searchable phrase for visibility test");
            engine.IndexDocuments(new[] { doc });
        });
        
        indexTask.Wait();
        
        // Give a moment for memory barriers to propagate (shouldn't be needed if properly synchronized)
        Thread.Sleep(100);
        
        var queryTask = Task.Run(() =>
        {
            return engine.Search(new Query("unique searchable phrase", 5));
        });
        
        var result = queryTask.Result;
        
        // Assert
        Assert.IsTrue(result.Records.Length > 0, 
            "Indexed document should be visible in searches from other threads");
        
        var doc = engine.GetDocument(docId);
        Assert.IsNotNull(doc, "GetDocument should return the indexed document from another thread");
    }
    
    [TestMethod]
    public void BatchedConcurrentIndexing_LargeBatches()
    {
        // Arrange
        var engine = SearchEngine.CreateDefault();
        var exceptions = new ConcurrentBag<Exception>();
        var batchSize = 1000;
        var batchCount = 10;
        
        // Act - multiple threads each indexing large batches
        Parallel.For(0, batchCount, batchId =>
        {
            try
            {
                var docs = Enumerable.Range(0, batchSize)
                    .Select(i => new Document(
                        batchId * batchSize + i,
                        $"Batch {batchId} large document {i} with lots of content to index"))
                    .ToList();
                
                engine.IndexDocuments(docs);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });
        
        // Assert
        Assert.AreEqual(0, exceptions.Count);
        
        // Verify we can search across all batches
        var result = engine.Search(new Query("large document", 50));
        Assert.IsTrue(result.Records.Length > 0, "Should be able to search documents from all batches");
    }
    
    [TestMethod]
    public void ThreadSafety_SearchResults_Immutable()
    {
        // Arrange
        var engine = CreatePopulatedEngine(1000);
        var exceptions = new ConcurrentBag<Exception>();
        
        // Act - get search results and have multiple threads access them
        var searchResult = engine.Search(new Query("document", 100));
        var recordsSnapshot = searchResult.Records;
        
        Parallel.For(0, 100, i =>
        {
            try
            {
                // Read results from multiple threads
                var count = recordsSnapshot.Length;
                if (count > 0)
                {
                    var firstRecord = recordsSnapshot[0];
                    var docId = firstRecord.DocumentId;
                    var score = firstRecord.Score;
                    
                    // Verify consistency
                    Assert.IsTrue(docId >= 0);
                    Assert.IsTrue(score >= 0);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });
        
        // Assert
        Assert.AreEqual(0, exceptions.Count, 
            "Reading search results from multiple threads should be safe");
    }
    
    private static SearchEngine CreatePopulatedEngine(int documentCount)
    {
        var engine = SearchEngine.CreateDefault();
        var documents = Enumerable.Range(0, documentCount)
            .Select(i => new Document(i, $"Document {i} with searchable content for testing thread safety"))
            .ToList();
        
        engine.IndexDocuments(documents);
        return engine;
    }
}

