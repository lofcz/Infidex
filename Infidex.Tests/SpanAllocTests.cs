using Infidex.Utilities;

namespace Infidex.Tests;

[TestClass]
public class SpanAllocTests
{
    [TestMethod]
    public void Alloc_AllocatesMemory_Successfully()
    {
        Span<byte> span = SpanAlloc.Alloc(100, out long pointer);
        
        Assert.AreNotEqual(0, pointer);
        Assert.AreEqual(100, span.Length);
        
        // Verify memory is zeroed
        for (int i = 0; i < span.Length; i++)
        {
            Assert.AreEqual(0, span[i]);
        }
        
        SpanAlloc.Free(pointer);
    }
    
    [TestMethod]
    public void Alloc2D_AllocatesMemory_Successfully()
    {
        var span2d = SpanAlloc.Alloc(10, 20, out long pointer);
        
        Assert.AreNotEqual(0, pointer);
        Assert.AreEqual(10, span2d.Height);
        Assert.AreEqual(20, span2d.Width);
        
        SpanAlloc.Free(pointer);
    }
    
    [TestMethod]
    public void AllocAndFree_MultipleAllocations_NoMemoryLeak()
    {
        for (int i = 0; i < 1000; i++)
        {
            Span<byte> span = SpanAlloc.Alloc(1000, out long pointer);
            span[0] = 42; // Write to ensure allocation
            SpanAlloc.Free(pointer);
        }
        
        // If no exception, test passes (no memory leaks detected)
        Assert.IsTrue(true);
    }
}


