using System;
using System.IO;
using System.Runtime.InteropServices;
using Infidex.Indexing.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Infidex.Tests;

[TestClass]
public class CompactArrayTests
{
    [TestMethod]
    public void TestBasicEncodingDecoding()
    {
        long[] values = [5, 2, 9, 100, 0, 5, 10, 90, 9, 1, 65, 10];
        
        CompactArray arr = CompactArray.Create(values);
        
        Assert.AreEqual(values.Length, arr.Count);
        Assert.AreEqual(7, arr.Width);

        for (int i = 0; i < values.Length; i++)
        {
            Assert.AreEqual((ulong)values[i], arr.Get(i));
        }
    }

    [TestMethod]
    public void TestEmpty()
    {
        long[] values = [];
        CompactArray arr = CompactArray.Create(values);
        Assert.AreEqual(0, arr.Count);
        Assert.AreEqual(1, arr.Width);
    }

    [TestMethod]
    public void TestZeroes()
    {
        long[] values = [0, 0, 0, 0];
        CompactArray arr = CompactArray.Create(values);
        Assert.AreEqual(4, arr.Count);
        Assert.AreEqual(1, arr.Width);
        
        for (int i = 0; i < values.Length; i++)
        {
            Assert.AreEqual(0UL, arr.Get(i));
        }
    }

    [TestMethod]
    public void TestLargeValues()
    {
        long[] values = [unchecked((long)ulong.MaxValue), 0, unchecked((long)(ulong.MaxValue >> 1)), 1234567890123456789];
        
        CompactArray arr = CompactArray.Create(values);
        
        Assert.AreEqual(64, arr.Width);
        
        for (int i = 0; i < values.Length; i++)
        {
            Assert.AreEqual((ulong)values[i], arr.Get(i));
        }
    }

    [TestMethod]
    public void TestBoundaryCrossing()
    {
        long[] values = [1L << 32, (1L << 32) | 1, 12345];
        
        CompactArray arr = CompactArray.Create(values);
        Assert.IsTrue(arr.Width >= 33);
        
        for (int i = 0; i < values.Length; i++)
        {
            Assert.AreEqual((ulong)values[i], arr.Get(i));
        }
    }

    [TestMethod]
    public void TestSerialization()
    {
        long[] values = [5, 2, 9, 100, 0, 5, 10, 90, 9, 1, 65, 10];
        CompactArray original = CompactArray.Create(values);

        using MemoryStream ms = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(ms);
        original.Write(writer);
        
        ms.Position = 0;
        using BinaryReader reader = new BinaryReader(ms);
        CompactArray loaded = CompactArray.Read(reader);
        
        Assert.AreEqual(original.Count, loaded.Count);
        Assert.AreEqual(original.Width, loaded.Width);
        
        for (int i = 0; i < values.Length; i++)
        {
            Assert.AreEqual(original.Get(i), loaded.Get(i));
        }
    }

    [TestMethod]
    public void TestOptimizedSerializationIntegrity()
    {
        long[] values = new long[1000];
        for(int i=0; i<1000; i++) values[i] = (long)i * 123456789;
        CompactArray original = CompactArray.Create(values);
        
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(ms))
        {
            writer.Write(original.Width);
            writer.Write(original.Count);
            writer.Write(original.Data.Length);
            for(int i=0; i<original.Data.Length; i++) writer.Write(original.Data[i]);
            
            ms.Position = 0;
            using (BinaryReader reader = new BinaryReader(ms))
            {
                CompactArray loaded = CompactArray.Read(reader);
                
                Assert.AreEqual(original.Count, loaded.Count);
                for(int i=0; i<values.Length; i++) Assert.AreEqual(values[i], (long)loaded.Get(i));
            }
        }

        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(ms))
        {
            original.Write(writer);
            
            ms.Position = 0;
            using (BinaryReader reader = new BinaryReader(ms))
            {
                int width = reader.ReadInt32();
                int count = reader.ReadInt32();
                int dataLen = reader.ReadInt32();
                
                Assert.AreEqual(original.Width, width);
                Assert.AreEqual(original.Count, count);
                Assert.AreEqual(original.Data.Length, dataLen);
                
                ulong[] data = new ulong[dataLen];
                for(int i=0; i<dataLen; i++) data[i] = reader.ReadUInt64();
                
                CompactArray loaded = new CompactArray(data, width, count);
                for(int i=0; i<values.Length; i++) Assert.AreEqual(values[i], (long)loaded.Get(i));
            }
        }
        
        if (BitConverter.IsLittleEndian)
        {
            byte[] manualBytes;
            byte[] optimizedBytes;
            
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                writer.Write(original.Width);
                writer.Write(original.Count);
                writer.Write(original.Data.Length);
                for(int i=0; i<original.Data.Length; i++) writer.Write(original.Data[i]);
                manualBytes = ms.ToArray();
            }
            
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                original.Write(writer);
                optimizedBytes = ms.ToArray();
            }
            
            CollectionAssert.AreEqual(manualBytes, optimizedBytes, "Optimized serialization produced different bytes than standard loop!");
        }
    }
}
