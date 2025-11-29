using Infidex.Indexing.Compression;

namespace Infidex.Tests;

[TestClass]
public class DArrayTests
{
    [TestMethod]
    public void TestDenseBitSet()
    {
        int n = 10000;
        BitSet bitSet = new BitSet(n);
        List<int> positions = [];
        
        Random r = new Random(42);
        for (int i = 0; i < n; i++)
        {
            if (r.NextDouble() < 0.5)
            {
                bitSet.Set(i);
                positions.Add(i);
            }
        }

        DArray dArray = DArray.Build(bitSet, select1: true);

        for (int i = 0; i < positions.Count; i++)
        {
            long pos = dArray.Select(bitSet, i);
            Assert.AreEqual(positions[i], pos, $"Failed at index {i}");
        }
    }

    [TestMethod]
    public void TestSparseBitSet()
    {
        int n = 100000;
        BitSet bitSet = new BitSet(n);
        List<int> positions = [];
        
        Random r = new Random(42);
        for (int i = 0; i < n; i++)
        {
            if (r.NextDouble() < 0.01) // 1% set bits
            {
                bitSet.Set(i);
                positions.Add(i);
            }
        }

        DArray dArray = DArray.Build(bitSet, select1: true);

        for (int i = 0; i < positions.Count; i++)
        {
            long pos = dArray.Select(bitSet, i);
            Assert.AreEqual(positions[i], pos, $"Failed at index {i}");
        }
    }

    [TestMethod]
    public void TestSelect0()
    {
        int n = 1000;
        BitSet bitSet = new BitSet(n);
        // Initially all 0s. Let's set some to 1 to make it interesting.
        // We want to find positions of 0s.
        
        // Set all to 1 first? No, default is 0.
        // Let's set indices 10, 20, 30 to 1.
        // 0s are at 0..9, 11..19, 21..29, 31..999
        
        bitSet.Set(10);
        bitSet.Set(20);
        bitSet.Set(30);
        
        DArray dArray = DArray.Build(bitSet, select1: false);
        
        // 0th zero is at 0
        Assert.AreEqual(0, dArray.Select(bitSet, 0));
        // 9th zero is at 9
        Assert.AreEqual(9, dArray.Select(bitSet, 9));
        // 10th zero is at 11 (since 10 is set)
        Assert.AreEqual(11, dArray.Select(bitSet, 10));
    }

    [TestMethod]
    public void TestSerialization()
    {
        int n = 10000;
        BitSet bitSet = new BitSet(n);
        Random r = new Random(123);
        for (int i = 0; i < n; i++)
        {
            if (r.NextDouble() < 0.5) bitSet.Set(i);
        }

        DArray original = DArray.Build(bitSet, select1: true);

        using MemoryStream ms = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(ms);
        original.Write(writer);
        
        ms.Position = 0;
        using BinaryReader reader = new BinaryReader(ms);
        DArray loaded = DArray.Read(reader, select1: true);
        
        // Test a few select operations
        int count = bitSet.PopCount();
        for (int i = 0; i < count; i += 100)
        {
            Assert.AreEqual(original.Select(bitSet, i), loaded.Select(bitSet, i));
        }
    }
}
