using Infidex.Core;
using Infidex.Indexing.Segments;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Infidex.Tests;

[TestClass]
public class SegmentTests
{
    [TestMethod]
    public void WriteAndReadSegment_ShouldWork()
    {
        var terms = new TermCollection();
        
        // Term "apple": Doc 1 (wt 10), Doc 3 (wt 20)
        var t1 = terms.CountTermUsage("apple", 100);
        t1.FirstCycleAdd(1, 100, false, 10.0f);
        t1.FirstCycleAdd(3, 100, false, 20.0f);
        
        // Term "banana": Doc 2 (wt 5)
        var t2 = terms.CountTermUsage("banana", 100);
        t2.FirstCycleAdd(2, 100, false, 5.0f);

        string path = "test_segment.seg";
        if (File.Exists(path)) File.Delete(path);

        var writer = new SegmentWriter();
        writer.WriteSegment(terms, 5, 0, path); // 5 docs total

        using (var reader = new SegmentReader(path))
        {
            Assert.AreEqual(2, reader.FstIndex.TermCount);
            Assert.AreEqual(5, reader.DocCount);

            var applePostings = reader.GetPostings("apple");
            Assert.IsNotNull(applePostings);
            Assert.AreEqual(2, applePostings.Value.DocIds.Length);
            Assert.AreEqual(1, applePostings.Value.DocIds[0]);
            Assert.AreEqual(3, applePostings.Value.DocIds[1]);
            Assert.AreEqual((byte)10, applePostings.Value.Weights[0]);

            var bananaPostings = reader.GetPostings("banana");
            Assert.IsNotNull(bananaPostings);
            Assert.AreEqual(1, bananaPostings.Value.DocIds.Length);
            Assert.AreEqual(2, bananaPostings.Value.DocIds[0]);

            Assert.IsNull(reader.GetPostings("orange"));
        }
        
        File.Delete(path);
    }

    [TestMethod]
    public void MergeSegments_ShouldWork()
    {
        string seg1Path = "seg1.seg";
        string seg2Path = "seg2.seg";
        string mergedPath = "merged.seg";

        // Segment 1 (Docs 0-4)
        var terms1 = new TermCollection();
        var t1 = terms1.CountTermUsage("common", 100);
        t1.FirstCycleAdd(1, 100, false, 10f);
        var t2 = terms1.CountTermUsage("unique1", 100);
        t2.FirstCycleAdd(2, 100, false, 20f);
        
        var writer = new SegmentWriter();
        writer.WriteSegment(terms1, 5, 0, seg1Path);

        // Segment 2 (Docs 0-4 -> mapped to 5-9)
        var terms2 = new TermCollection();
        var t3 = terms2.CountTermUsage("common", 100);
        t3.FirstCycleAdd(0, 100, false, 30f); // Becomes Doc 5
        var t4 = terms2.CountTermUsage("unique2", 100);
        t4.FirstCycleAdd(3, 100, false, 40f); // Becomes Doc 8
        
        writer.WriteSegment(terms2, 5, 0, seg2Path);

        // Merge
        var merger = new SegmentMerger();
        var readers = new List<SegmentReader>
        {
            new SegmentReader(seg1Path),
            new SegmentReader(seg2Path)
        };
        
        merger.MergeSegments(readers, mergedPath);
        
        foreach(var r in readers) r.Dispose();

        // Verify Merged
        using (var reader = new SegmentReader(mergedPath))
        {
            Assert.AreEqual(3, reader.FstIndex.TermCount); // common, unique1, unique2
            Assert.AreEqual(10, reader.DocCount);

            var common = reader.GetPostings("common");
            Assert.IsNotNull(common);
            Assert.AreEqual(2, common.Value.DocIds.Length);
            Assert.AreEqual(1, common.Value.DocIds[0]); // From Seg1
            Assert.AreEqual(5, common.Value.DocIds[1]); // From Seg2 (0 + 5)
            Assert.AreEqual((byte)10, common.Value.Weights[0]);
            Assert.AreEqual((byte)30, common.Value.Weights[1]);

            var unique1 = reader.GetPostings("unique1");
            Assert.AreEqual(1, unique1.Value.DocIds.Length);
            Assert.AreEqual(2, unique1.Value.DocIds[0]);

            var unique2 = reader.GetPostings("unique2");
            Assert.AreEqual(1, unique2.Value.DocIds.Length);
            Assert.AreEqual(8, unique2.Value.DocIds[0]); // From Seg2 (3 + 5)
        }

        File.Delete(seg1Path);
        File.Delete(seg2Path);
        File.Delete(mergedPath);
    }
}
