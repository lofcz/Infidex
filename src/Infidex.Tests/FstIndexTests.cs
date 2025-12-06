using System.Buffers;
using Infidex.Indexing.Fst;

namespace Infidex.Tests;

[TestClass]
public class FstIndexTests
{
    private static FstIndex CreateIndex(string[] terms)
    {
        var builder = new FstBuilder();
        Array.Sort(terms, StringComparer.Ordinal);
        for (int i = 0; i < terms.Length; i++)
        {
            builder.Add(terms[i], i);
        }
        return builder.Build();
    }

    [TestMethod]
    public void MatchWithinEditDistance1_FindsMatches()
    {
        var terms = new[] { "apple", "apply", "apples", "bpple", "capple" };
        var fst = CreateIndex(terms);
        
        Span<int> buffer = stackalloc int[10];
        
        int count = fst.MatchWithinEditDistance1("applz", buffer);
        
        Assert.IsTrue(count >= 2, "Should match at least apple and apply");
        
        var builder = new FstBuilder();
        builder.Add("apple", 10);
        builder.Add("apples", 20);
        builder.Add("apply", 30);
        builder.Add("bpple", 40);
        
        fst = builder.Build();
        
        count = fst.MatchWithinEditDistance1("applz", buffer);
        Assert.AreEqual(2, count);
        
        var results = buffer.Slice(0, count).ToArray();
        CollectionAssert.Contains(results, 10); // apple
        CollectionAssert.Contains(results, 30); // apply
        
        count = fst.MatchWithinEditDistance1("apple", buffer);
        
        Assert.AreEqual(4, count);
        results = buffer.Slice(0, count).ToArray();
        CollectionAssert.Contains(results, 10);
        CollectionAssert.Contains(results, 20);
        CollectionAssert.Contains(results, 30);
        CollectionAssert.Contains(results, 40);
    }
    
    [TestMethod]
    public void MatchWithinEditDistance1_BufferOverflow()
    {
        var builder = new FstBuilder();
        builder.Add("apple", 1);
        builder.Add("apply", 2);
        builder.Add("bpple", 3);
        var fst = builder.Build();
        
        Span<int> buffer = stackalloc int[1];
        
        // "apple" matches all 3 (apple->0, apply->1, bpple->1)
        int count = fst.MatchWithinEditDistance1("apple", buffer);
        
        Assert.AreEqual(3, count, "Should return total count even if buffer is small");
    }

    [TestMethod]
    public void GetByPrefix_FillsBufferAndStops()
    {
        var builder = new FstBuilder();
        builder.Add("apple", 1);
        builder.Add("apply", 2);
        builder.Add("bpple", 3);
        var fst = builder.Build();
        
        Span<int> buffer = stackalloc int[1];
        
        // "app" matches apple (1) and apply (2)
        // Buffer size 1 -> should return 1 (stopped when full)
        int count = fst.GetByPrefix("app", buffer);
        
        Assert.AreEqual(1, count);
        Assert.IsTrue(buffer[0] == 1 || buffer[0] == 2);
        
        buffer = stackalloc int[5];
        count = fst.GetByPrefix("app", buffer);
        Assert.AreEqual(2, count);
        var results = buffer.Slice(0, count).ToArray();
        CollectionAssert.Contains(results, 1);
        CollectionAssert.Contains(results, 2);
    }

    [TestMethod]
    public void MatchWithinEditDistance1_LongQuery_FallsBackToSlowPath()
    {
        var builder = new FstBuilder();
        string longTerm = new string('a', 70); // 70 'a's
        string longTermVariant = new string('a', 69) + 'b'; // 69 'a's then 'b' (dist 1)
        string longTermDist2 = new string('a', 68) + "bb"; // 68 'a's then 'bb' (dist 2)
        
        builder.Add(longTerm, 100);
        builder.Add(longTermVariant, 200);
        builder.Add(longTermDist2, 300);
        
        var fst = builder.Build();
        
        Span<int> buffer = stackalloc int[10];
        
        // Exact match
        int count = fst.MatchWithinEditDistance1(longTerm, buffer);
        Assert.IsTrue(count >= 2, "Should match at least exact and variant (dist 1)");
        
        var results = buffer.Slice(0, count).ToArray();
        CollectionAssert.Contains(results, 100);
        CollectionAssert.Contains(results, 200);
        CollectionAssert.DoesNotContain(results, 300); // Dist 2 should not match
    }
}

