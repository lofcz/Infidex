using System.Collections;

namespace Infidex.Internalized.Roaring;

public class RoaringBitmap : IEnumerable<int>, IEquatable<RoaringBitmap>
{
    private readonly RoaringArray _highLowContainer;

    private RoaringBitmap(RoaringArray input)
    {
        _highLowContainer = input;
    }

    public long Cardinality => _highLowContainer.Cardinality;

    public IEnumerator<int> GetEnumerator()
    {
        return ToArray().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
    
    /// <summary>
    /// Convert the bitmap to an array of integers
    /// </summary>
    /// <returns>Array of integers</returns>
    public List<int> ToArray()
    {
        List<int> list = new List<int>((int)Cardinality);
        _highLowContainer.EnumerateFill(list);
        return list;
    }
    
    public bool Equals(RoaringBitmap? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        return !ReferenceEquals(null, other) && _highLowContainer.Equals(other._highLowContainer);
    }
    
    public override bool Equals(object? obj)
    {
        RoaringBitmap? ra = obj as RoaringBitmap;
        return ra != null && Equals(ra);
    }

    public override int GetHashCode()
    {
        return (13 ^ _highLowContainer.GetHashCode()) << 3;
    }

    /// <summary>
    /// Creates a new immutable RoaringBitmap from an existing list of integers
    /// </summary>
    /// <param name="values">List of integers</param>
    /// <returns>RoaringBitmap</returns>
    public static RoaringBitmap Create(params int[] values)
    {
        return Create(values.AsEnumerable());
    }

    /// <summary>
    /// Optimizes a RoaringBitmap to prepare e.g. for Serialization/Deserialization
    /// </summary>
    /// <returns>RoaringBitmap</returns>
    public RoaringBitmap Optimize()
    {
        return new RoaringBitmap(RoaringArray.Optimize(_highLowContainer));
    }

    /// <summary>
    /// Creates a new immutable RoaringBitmap from an existing list of integers
    /// </summary>
    /// <param name="values">List of integers</param>
    /// <returns>RoaringBitmap</returns>
    public static RoaringBitmap Create(IEnumerable<int> values)
    {
        int[] data = values as int[] ?? values.ToArray();
        if (data.Length == 0)
        {
            return new RoaringBitmap(new RoaringArray(0, [], []));
        }

        Array.Sort(data);

        // In-place deduplication (two pointers technique)
        int uniqueCount = 1;
        for (int i = 1; i < data.Length; i++)
        {
            if (data[i] != data[uniqueCount - 1])
            {
                data[uniqueCount++] = data[i];
            }
        }

        List<ushort> keys = [];
        List<Container> containers = [];
        int index = 0;

        while (index < uniqueCount)
        {
            ushort hb = Utils.HighBits(data[index]);
            int start = index;
            index++;
            while (index < uniqueCount && Utils.HighBits(data[index]) == hb)
            {
                index++;
            }

            int count = index - start;
            ushort[] lows = new ushort[count];
            for (int j = 0; j < count; j++)
            {
                lows[j] = Utils.LowBits(data[start + j]);
            }

            keys.Add(hb);
            containers.Add(count > Container.MaxSize
                ? BitmapContainer.Create(lows)
                : ArrayContainer.Create(lows));
        }

        return new RoaringBitmap(new RoaringArray(keys.Count, keys, containers));
    }

    /// <summary>
    /// Bitwise Or operation of two RoaringBitmaps
    /// </summary>
    /// <param name="x">RoaringBitmap</param>
    /// <param name="y">RoaringBitmap</param>
    /// <returns>RoaringBitmap</returns>
    public static RoaringBitmap operator |(RoaringBitmap x, RoaringBitmap y)
    {
        return new RoaringBitmap(x._highLowContainer | y._highLowContainer);
    }

    /// <summary>
    /// Bitwise And operation of two RoaringBitmaps
    /// </summary>
    /// <param name="x">RoaringBitmap</param>
    /// <param name="y">RoaringBitmap</param>
    /// <returns>RoaringBitmap</returns>
    public static RoaringBitmap operator &(RoaringBitmap x, RoaringBitmap y)
    {
        return new RoaringBitmap(x._highLowContainer & y._highLowContainer);
    }

    /// <summary>
    /// Bitwise Not operation of a RoaringBitmap
    /// </summary>
    /// <param name="x">RoaringBitmap</param>
    /// <returns>RoaringBitmap</returns>
    public static RoaringBitmap operator ~(RoaringBitmap x)
    {
        return new RoaringBitmap(~x._highLowContainer);
    }

    /// <summary>
    /// Bitwise Xor operation of two RoaringBitmaps
    /// </summary>
    /// <param name="x">RoaringBitmap</param>
    /// <param name="y">RoaringBitmap</param>
    /// <returns>RoaringBitmap</returns>
    public static RoaringBitmap operator ^(RoaringBitmap x, RoaringBitmap y)
    {
        return new RoaringBitmap(x._highLowContainer ^ y._highLowContainer);
    }

    /// <summary>
    /// Bitwise AndNot operation of two RoaringBitmaps
    /// </summary>
    /// <param name="x">RoaringBitmap</param>
    /// <param name="y">RoaringBitmap</param>
    /// <returns>RoaringBitmap</returns>
    public static RoaringBitmap AndNot(RoaringBitmap x, RoaringBitmap y)
    {
        return new RoaringBitmap(RoaringArray.AndNot(x._highLowContainer, y._highLowContainer));
    }

    /// <summary>
    /// Serializes a RoaringBitmap into a stream using the 'official' RoaringBitmap file format
    /// </summary>
    /// <param name="roaringBitmap">RoaringBitmap</param>
    /// <param name="stream">Stream</param>
    public static void Serialize(RoaringBitmap roaringBitmap, Stream stream)
    {
        RoaringArray.Serialize(roaringBitmap._highLowContainer, stream);
    }

    /// <summary>
    /// Deserializes a RoaringBitmap from astream using the 'official' RoaringBitmap file format
    /// </summary>
    /// <param name="stream">Stream</param>
    public static RoaringBitmap Deserialize(Stream stream)
    {
        RoaringArray ra = RoaringArray.Deserialize(stream);
        return new RoaringBitmap(ra);
    }
}
