using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Infidex.Internalized.Roaring;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Infidex.Tests;

[TestClass]
public class RoaringBitmapTests
{
    [TestMethod]
    public void TestBasicOperations()
    {
        int[] values = [1, 5, 10, 100, 1000, 50000, 70000];
        RoaringBitmap rb = RoaringBitmap.Create(values);
        
        Assert.AreEqual(values.Length, rb.Cardinality);
        
        List<int> result = rb.ToArray();
        CollectionAssert.AreEqual(values, result);
    }

    [TestMethod]
    public void TestArrayContainer()
    {
        // Less than 4096 elements should be ArrayContainer
        List<int> values = Enumerable.Range(0, 100).Select(x => x * 2).ToList();
        RoaringBitmap rb = RoaringBitmap.Create(values);
        
        Assert.AreEqual(100, rb.Cardinality);
        CollectionAssert.AreEqual(values, rb.ToArray());
    }

    [TestMethod]
    public void TestBitmapContainer()
    {
        // More than 4096 elements in a chunk (0-65535) should be BitmapContainer
        // Let's create a dense range
        List<int> values = Enumerable.Range(0, 5000).ToList();
        RoaringBitmap rb = RoaringBitmap.Create(values);
        
        Assert.AreEqual(5000, rb.Cardinality);
        CollectionAssert.AreEqual(values, rb.ToArray());
    }

    [TestMethod]
    public void TestOrOperation()
    {
        RoaringBitmap r1 = RoaringBitmap.Create(1, 2, 3);
        RoaringBitmap r2 = RoaringBitmap.Create(3, 4, 5);
        RoaringBitmap result = r1 | r2;
        
        CollectionAssert.AreEqual(new int[] { 1, 2, 3, 4, 5 }, result.ToArray());
    }

    [TestMethod]
    public void TestAndOperation()
    {
        RoaringBitmap r1 = RoaringBitmap.Create(1, 2, 3);
        RoaringBitmap r2 = RoaringBitmap.Create(3, 4, 5);
        RoaringBitmap result = r1 & r2;
        
        CollectionAssert.AreEqual(new int[] { 3 }, result.ToArray());
    }

    [TestMethod]
    public void TestXorOperation()
    {
        RoaringBitmap r1 = RoaringBitmap.Create(1, 2, 3);
        RoaringBitmap r2 = RoaringBitmap.Create(3, 4, 5);
        RoaringBitmap result = r1 ^ r2;
        
        CollectionAssert.AreEqual(new int[] { 1, 2, 4, 5 }, result.ToArray());
    }

    [TestMethod]
    public void TestAndNotOperation()
    {
        RoaringBitmap r1 = RoaringBitmap.Create(1, 2, 3);
        RoaringBitmap r2 = RoaringBitmap.Create(3, 4, 5);
        RoaringBitmap result = RoaringBitmap.AndNot(r1, r2);
        
        CollectionAssert.AreEqual(new int[] { 1, 2 }, result.ToArray());
    }

    [TestMethod]
    public void TestSerialization()
    {
        // Mix of Array and Bitmap containers
        List<int> values = new List<int>();
        values.AddRange(Enumerable.Range(0, 5000)); // BitmapContainer
        values.AddRange(Enumerable.Range(70000, 100)); // ArrayContainer
        
        RoaringBitmap original = RoaringBitmap.Create(values);

        using MemoryStream ms = new MemoryStream();
        RoaringBitmap.Serialize(original, ms);
        
        ms.Position = 0;
        RoaringBitmap loaded = RoaringBitmap.Deserialize(ms);
        
        Assert.AreEqual(original.Cardinality, loaded.Cardinality);
        CollectionAssert.AreEqual(original.ToArray(), loaded.ToArray());
    }
    
    [TestMethod]
    public void TestRunContainerScenario()
    {
        // RoaringBitmap reference implementation supports run containers but we didn't explicitly implement run container optimization (RLE) in Create/Optimize yet,
        // but Deserialize handles it.
        // Let's ensure if we serialize/deserialize we are consistent.
        // We can test basic functionality.
        
        List<int> values = Enumerable.Range(0, 1000).ToList();
        RoaringBitmap rb = RoaringBitmap.Create(values);
        // Optimize might not do much if we didn't implement RunContainer creation logic in Optimize, 
        // but it should at least return a valid bitmap.
        RoaringBitmap opt = rb.Optimize();
        
        Assert.AreEqual(rb.Cardinality, opt.Cardinality);
        CollectionAssert.AreEqual(rb.ToArray(), opt.ToArray());
    }

    [TestMethod]
    public void TestBitmapContainerOperations()
    {
        // Create two dense bitmaps that use BitmapContainer (> 4096 elements)
        // Range 0..5000
        var list1 = Enumerable.Range(0, 5000).ToList();
        var rb1 = RoaringBitmap.Create(list1);

        // Range 4000..9000
        var list2 = Enumerable.Range(4000, 5000).ToList();
        var rb2 = RoaringBitmap.Create(list2);

        // Intersection: 4000..5000 (1000 elements) -> ArrayContainer likely result, but source is BitmapContainer.
        // Operation: BitmapContainer & BitmapContainer -> BitmapContainer (then potentially converted to ArrayContainer)
        // Utils.Popcnt will be called during BitmapContainer intersection if it returns a BitmapContainer or during cardinality check.
        // Actually BitmapContainer operator & returns Container. It clones, ANDs, counts cardinality.
        // Cardinality check uses Utils.Popcnt.
        
        var intersection = rb1 & rb2;
        Assert.AreEqual(1000, intersection.Cardinality);
        
        // Union: 0..9000 (9000 elements) -> BitmapContainer
        var union = rb1 | rb2;
        Assert.AreEqual(9000, union.Cardinality);
        
        // XOR: 0..4000 and 5000..9000 (4000 + 4000 = 8000 elements) -> BitmapContainer
        var xor = rb1 ^ rb2;
        Assert.AreEqual(8000, xor.Cardinality);
        
        // Difference: 0..4000 (4000 elements) -> ArrayContainer (4000 <= 4096)
        var diff = RoaringBitmap.AndNot(rb1, rb2);
        Assert.AreEqual(4000, diff.Cardinality);
    }
}
