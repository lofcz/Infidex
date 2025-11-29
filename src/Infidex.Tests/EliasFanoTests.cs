using System;
using System.IO;
using Infidex.Indexing.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Infidex.Tests;

[TestClass]
public class EliasFanoTests
{
    [TestMethod]
    public void TestEncodeDecode()
    {
        long[] values = [1, 5, 10, 100, 1000, 1234, 5000];
        EliasFano ef = EliasFano.Encode(values);
        
        Assert.AreEqual(values.Length, ef.Count);
        
        for (int i = 0; i < values.Length; i++)
        {
            Assert.AreEqual(values[i], ef.Get(i));
        }
    }

    [TestMethod]
    public void TestEmpty()
    {
        long[] values = [];
        EliasFano ef = EliasFano.Encode(values);
        Assert.AreEqual(0, ef.Count);
    }

    [TestMethod]
    public void TestRandomData()
    {
        int n = 10000;
        long[] values = new long[n];
        Random r = new Random(12345);
        long current = 0;
        for (int i = 0; i < n; i++)
        {
            current += r.Next(1, 50); // Strictly increasing
            values[i] = current;
        }

        EliasFano ef = EliasFano.Encode(values);
        
        for (int i = 0; i < n; i++)
        {
            Assert.AreEqual(values[i], ef.Get(i), $"Failed at index {i}");
        }
    }

    [TestMethod]
    public void TestSerialization()
    {
        long[] values = [1, 5, 10, 100, 1000, 1234, 5000];
        EliasFano original = EliasFano.Encode(values);

        using MemoryStream ms = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(ms);
        original.Write(writer);
        
        ms.Position = 0;
        using BinaryReader reader = new BinaryReader(ms);
        EliasFano loaded = EliasFano.Read(reader);
        
        Assert.AreEqual(original.Count, loaded.Count);
        
        for (int i = 0; i < values.Length; i++)
        {
            Assert.AreEqual(original.Get(i), loaded.Get(i));
        }
    }
}
