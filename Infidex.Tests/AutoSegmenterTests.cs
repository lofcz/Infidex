using Infidex.Core;

namespace Infidex.Tests;

[TestClass]
public class AutoSegmenterTests
{
    [TestMethod]
    public void SegmentSingleDocument_ShortText_NoSegmentation()
    {
        var delimiters = new[] { ' ', '.' };
        var segmenter = new AutoSegmenter(0.2, 200, delimiters);
        
        var sourceDoc = new CoreDocument(1L, 0, "This is a short document.", "", 0);
        var result = new List<CoreDocument>();
        var tracking = new Dictionary<long, List<int>>();
        
        segmenter.SegmentSingleDocument(result, sourceDoc, tracking, out bool wasSegmented);
        
        Assert.IsFalse(wasSegmented);
        Assert.AreEqual(1, result.Count);
    }
    
    [TestMethod]
    public void SegmentSingleDocument_LongText_CreatesSegments()
    {
        var delimiters = new[] { ' ', '.' };
        var segmenter = new AutoSegmenter(0.2, 50, delimiters); // Small target size
        
        string longText = string.Join(" ", Enumerable.Repeat("word", 100));
        var sourceDoc = new CoreDocument(1L, 0, longText, "", 0);
        var result = new List<CoreDocument>();
        var tracking = new Dictionary<long, List<int>>();
        
        segmenter.SegmentSingleDocument(result, sourceDoc, tracking, out bool wasSegmented);
        
        Assert.IsTrue(wasSegmented);
        Assert.IsTrue(result.Count > 1);
        
        // First segment should have original text in Reserved
        Assert.IsNotNull(result[0].Reserved);
        Assert.AreEqual(longText, result[0].Reserved);
        
        // Segments should have correct numbering
        for (int i = 0; i < result.Count; i++)
        {
            Assert.AreEqual(i, result[i].SegmentNumber);
            Assert.AreEqual(1L, result[i].DocumentKey);
        }
    }
    
    [TestMethod]
    public void SegmentsRequired_MixedLengths_DetectsCorrectly()
    {
        var docs = new List<CoreDocument>
        {
            new CoreDocument(1L, 0, "short", "", 0),
            new CoreDocument(2L, 0, new string('x', 500), "", 0),
            new CoreDocument(3L, 0, "also short", "", 0)
        };
        
        bool required = AutoSegmenter.SegmentsRequired(docs, 100);
        
        Assert.IsTrue(required);
    }
}


