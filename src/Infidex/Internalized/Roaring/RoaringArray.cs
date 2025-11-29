using System.Text;

namespace Infidex.Internalized.Roaring;

internal class RoaringArray : IEquatable<RoaringArray>
{
    private const int SerialCookieNoRuncontainer = 12346;
    private const int SerialCookie = 12347;
    private const int NoOffsetThreshold = 4;
    private readonly ushort[] _keys;
    private readonly int _size;
    private readonly Container[] _values;

    /// <summary>
    /// Use List directly, because the enumerator is a struct
    /// </summary>
    internal RoaringArray(int size, List<ushort> keys, List<Container> containers)
    {
        _size = size;
        _keys = new ushort[_size];
        _values = new Container[_size];
        for (int i = 0; i < _size; i++)
        {
            _keys[i] = keys[i];
            _values[i] = containers[i];
            Cardinality += _values[i].Cardinality;
        }
    }

    private RoaringArray(int size, ushort[] keys, Container[] containers)
    {
        _size = size;
        _keys = keys;
        _values = containers;
        for (int i = 0; i < containers.Length; i++)
        {
            Cardinality += containers[i].Cardinality;
        }
    }

    public long Cardinality { get; }
    
    public void EnumerateFill(List<int> list)
    {
        for (int i = 0; i < _size; i++)
        {
            ushort key = _keys[i];
            int shiftedKey = key << 16;
            Container container = _values[i];
            container.EnumerateFill(list, shiftedKey);
        }
    }

    public bool Equals(RoaringArray? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        if (ReferenceEquals(null, other))
        {
            return false;
        }
        if (_size != other._size)
        {
            return false;
        }
        for (int i = 0; i < _size; i++)
        {
            if (_keys[i] != other._keys[i] || !_values[i].Equals(other._values[i]))
            {
                return false;
            }
        }
        return true;
    }
    
    public override bool Equals(object? obj)
    {
        RoaringArray? ra = obj as RoaringArray;
        return ra != null && Equals(ra);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int code = 17;
            code = code * 23 + _size;
            for (int i = 0; i < _size; i++)
            {
                code = code * 23 + _keys[i].GetHashCode();
                code = code * 23 + _values[i].GetHashCode();
            }
            return code;
        }
    }

    private int AdvanceUntil(ushort key, int index)
    {
        return Utils.AdvanceUntil(_keys, index, key);
    }

    public static RoaringArray operator |(RoaringArray x, RoaringArray y)
    {
        int xLength = x._size;
        int yLength = y._size;
        List<ushort> keys = new List<ushort>(xLength + yLength);
        List<Container> containers = new List<Container>(xLength + yLength);
        int size = 0;
        int xPos = 0;
        int yPos = 0;
        if (xPos < xLength && yPos < yLength)
        {
            ushort xKey = x._keys[xPos];
            ushort yKey = y._keys[yPos];
            while (true)
            {
                if (xKey == yKey)
                {
                    keys.Add(xKey);
                    containers.Add(x._values[xPos] | y._values[yPos]);
                    size++;
                    xPos++;
                    yPos++;
                    if (xPos == xLength || yPos == yLength)
                    {
                        break;
                    }
                    xKey = x._keys[xPos];
                    yKey = y._keys[yPos];
                }
                else if (xKey < yKey)
                {
                    keys.Add(xKey);
                    containers.Add(x._values[xPos]);
                    size++;
                    xPos++;
                    if (xPos == xLength)
                    {
                        break;
                    }
                    xKey = x._keys[xPos];
                }
                else
                {
                    keys.Add(yKey);
                    containers.Add(y._values[yPos]);
                    size++;
                    yPos++;
                    if (yPos == yLength)
                    {
                        break;
                    }
                    yKey = y._keys[yPos];
                }
            }
        }
        if (xPos == xLength)
        {
            for (int i = yPos; i < yLength; i++)
            {
                keys.Add(y._keys[i]);
                containers.Add(y._values[i]);
                size++;
            }
        }
        else if (yPos == yLength)
        {
            for (int i = xPos; i < xLength; i++)
            {
                keys.Add(x._keys[i]);
                containers.Add(x._values[i]);
                size++;
            }
        }
        return new RoaringArray(size, keys, containers);
    }

    public static RoaringArray operator &(RoaringArray x, RoaringArray y)
    {
        int xLength = x._size;
        int yLength = y._size;
        List<ushort>? keys = null;
        List<Container>? containers = null;
        int size = 0;
        int xPos = 0;
        int yPos = 0;
        while (xPos < xLength && yPos < yLength)
        {
            ushort xKey = x._keys[xPos];
            ushort yKey = y._keys[yPos];
            if (xKey == yKey)
            {
                Container c = x._values[xPos] & y._values[yPos];
                if (c.Cardinality > 0)
                {
                    if (keys == null)
                    {
                        int length = Math.Min(xLength, yLength);
                        keys = new List<ushort>(length);
                        containers = new List<Container>(length);
                    }
                    keys.Add(xKey);
                    containers!.Add(c);
                    size++;
                }
                xPos++;
                yPos++;
            }
            else if (xKey < yKey)
            {
                xPos = x.AdvanceUntil(yKey, xPos);
            }
            else
            {
                yPos = y.AdvanceUntil(xKey, yPos);
            }
        }
        return new RoaringArray(size, keys!, containers!);
    }

    public static RoaringArray operator ^(RoaringArray x, RoaringArray y)
    {
        int xLength = x._size;
        int yLength = y._size;
        List<ushort> keys = new List<ushort>(xLength + yLength);
        List<Container> containers = new List<Container>(xLength + yLength);
        int size = 0;
        int xPos = 0;
        int yPos = 0;
        if (xPos < xLength && yPos < yLength)
        {
            ushort xKey = x._keys[xPos];
            ushort yKey = y._keys[yPos];
            while (true)
            {
                if (xKey == yKey)
                {
                    Container c = x._values[xPos] ^ y._values[yPos];
                    if (c.Cardinality > 0)
                    {
                        keys.Add(xKey);
                        containers.Add(c);
                        size++;
                    }

                    xPos++;
                    yPos++;
                    if (xPos == xLength || yPos == yLength)
                    {
                        break;
                    }
                    xKey = x._keys[xPos];
                    yKey = y._keys[yPos];
                }
                else if (xKey < yKey)
                {
                    keys.Add(xKey);
                    containers.Add(x._values[xPos]);
                    size++;
                    xPos++;
                    if (xPos == xLength)
                    {
                        break;
                    }
                    xKey = x._keys[xPos];
                }
                else
                {
                    keys.Add(yKey);
                    containers.Add(y._values[yPos]);
                    size++;
                    yPos++;
                    if (yPos == yLength)
                    {
                        break;
                    }
                    yKey = y._keys[yPos];
                }
            }
        }
        if (xPos == xLength)
        {
            for (int i = yPos; i < yLength; i++)
            {
                keys.Add(y._keys[i]);
                containers.Add(y._values[i]);
                size++;
            }
        }
        else if (yPos == yLength)
        {
            for (int i = xPos; i < xLength; i++)
            {
                keys.Add(x._keys[i]);
                containers.Add(x._values[i]);
                size++;
            }
        }
        return new RoaringArray(size, keys, containers);
    }

    public static RoaringArray operator ~(RoaringArray x)
    {
        List<ushort> keys = new List<ushort>(Container.MaxCapacity);
        int size = 0;
        List<Container> containers = new List<Container>(Container.MaxCapacity);
        int oldIndex = 0;
        for (int i = 0; i < Container.MaxCapacity; i++)
        {
            ushort ushortI = (ushort) i;
            int index = Array.BinarySearch(x._keys, oldIndex, x._size - oldIndex, ushortI);
            if (index < 0)
            {
                keys.Add(ushortI);
                containers.Add(BitmapContainer.One);
                size++;
            }
            else
            {
                Container c = x._values[index];
                if (!c.Equals(BitmapContainer.One)) // the bitwise negation of the one container is the zero container
                {
                    Container nc = ~c;
                    if (nc.Cardinality > 0)
                    {
                        keys.Add(ushortI);
                        containers.Add(nc);
                        size++;
                    }
                }
                oldIndex = index;
            }
        }
        return new RoaringArray(size, keys, containers);
    }

    public static RoaringArray AndNot(RoaringArray x, RoaringArray y)
    {
        int xLength = x._size;
        int yLength = y._size;
        List<ushort> keys = new List<ushort>(xLength);
        List<Container> containers = new List<Container>(xLength);
        int size = 0;
        int xPos = 0;
        int yPos = 0;
        while (xPos < xLength && yPos < yLength)
        {
            ushort xKey = x._keys[xPos];
            ushort yKey = y._keys[yPos];
            if (xKey == yKey)
            {
                Container c = Container.AndNot(x._values[xPos], y._values[yPos]);
                if (c.Cardinality > 0)
                {
                    keys.Add(xKey);
                    containers.Add(c);
                    size++;
                }
                xPos++;
                yPos++;
            }
            else if (xKey < yKey)
            {
                int next = x.AdvanceUntil(yKey, xPos);
                for (int i = xPos; i < next; i++)
                {
                    keys.Add(x._keys[i]);
                    containers.Add(x._values[i]);
                    size++;
                }
                xPos = next;
            }
            else
            {
                yPos = y.AdvanceUntil(xKey, yPos);
            }
        }
        if (yPos == yLength)
        {
            for (int i = xPos; i < xLength; i++)
            {
                keys.Add(x._keys[i]);
                containers.Add(x._values[i]);
                size++;
            }
        }
        return new RoaringArray(size, keys, containers);
    }

    public static void Serialize(RoaringArray roaringArray, Stream stream)
    {
        bool hasRun = HasRunContainer(roaringArray);
        using BinaryWriter binaryWriter = new BinaryWriter(stream, Encoding.UTF8, true);
        int size = roaringArray._size;
        ushort[] keys = roaringArray._keys;
        Container[] values = roaringArray._values;
        int startOffset = 0;
        if (hasRun)
        {
            binaryWriter.Write(SerialCookie | ((size - 1) << 16));
            byte[] bitmapOfRunContainers = new byte[(size + 7) / 8];
            for (int i = 0; i < size; ++i)
            {
                if (values[i].Equals(ArrayContainer.One) || values[i].Equals(BitmapContainer.One))
                {
                    bitmapOfRunContainers[i / 8] |= (byte) (1 << (i % 8));
                }
            }
            binaryWriter.Write(bitmapOfRunContainers);
        }
        else // no run containers
        {
            binaryWriter.Write(SerialCookieNoRuncontainer);
            binaryWriter.Write(size);
            startOffset = 4 + 4 + 4 * size + 4 * size;
        }
        for (int k = 0; k < size; ++k)
        {
            binaryWriter.Write(keys[k]);
            binaryWriter.Write((ushort) (values[k].Cardinality - 1));
        }
        if (!hasRun || size >= NoOffsetThreshold)
        {
            for (int k = 0; k < size; k++)
            {
                binaryWriter.Write(startOffset);
                startOffset += values[k].ArraySizeInBytes;
            }
        }
        for (int k = 0; k < size; ++k)
        {
            Container container = values[k];
            ArrayContainer ac;
            BitmapContainer bc;
            if ((ac = container as ArrayContainer) != null)
            {
                if (ac.Equals(ArrayContainer.One))
                {
                    binaryWriter.Write((ushort) 1);
                    binaryWriter.Write((ushort) 0);
                    binaryWriter.Write((ushort) (Container.MaxSize - 1));
                }
                else
                {
                    ArrayContainer.Serialize(ac, binaryWriter);
                }
            }
            else if ((bc = container as BitmapContainer) != null)
            {
                if (bc.Equals(BitmapContainer.One))
                {
                    binaryWriter.Write((ushort) 1);
                    binaryWriter.Write((ushort) 0);
                    binaryWriter.Write((ushort) (Container.MaxCapacity - 1));
                }
                else
                {
                    BitmapContainer.Serialize(bc, binaryWriter);
                }
            }
        }
        binaryWriter.Flush();
    }

    private static bool HasRunContainer(RoaringArray roaringArray)
    {
        for (int i = 0; i < roaringArray._size; i++)
        {
            if (roaringArray._values[i].Equals(ArrayContainer.One) || roaringArray._values[i].Equals(BitmapContainer.One))
            {
                return true;
            }
        }
        return false;
    }

    public static RoaringArray Deserialize(Stream stream)
    {
        using BinaryReader binaryReader = new BinaryReader(stream, Encoding.UTF8, true);
        uint cookie = binaryReader.ReadUInt32();
        uint lbcookie = cookie & 0xFFFF;
        if (lbcookie != SerialCookie && cookie != SerialCookieNoRuncontainer)
        {
            throw new InvalidDataException("No RoaringBitmap file.");
        }
        bool hasRun = lbcookie == SerialCookie;
        int size = (int) (hasRun ? (cookie >> 16) + 1 : binaryReader.ReadUInt32());
        ushort[] keys = new ushort[size];
        Container[] containers = new Container[size];
        int[] cardinalities = new int[size];
        bool[] isBitmap = new bool[size];

        byte[]? bitmapOfRunContainers = null;
        if (hasRun)
        {
            bitmapOfRunContainers = binaryReader.ReadBytes((size + 7) / 8);
        }
        for (int k = 0; k < size; ++k)
        {
            keys[k] = binaryReader.ReadUInt16();
            cardinalities[k] = 1 + (0xFFFF & binaryReader.ReadUInt16());
            isBitmap[k] = cardinalities[k] > Container.MaxSize;
            if (bitmapOfRunContainers != null && (bitmapOfRunContainers[k / 8] & (1 << (k % 8))) != 0)
            {
                isBitmap[k] = false;
            }
        }
        if (!hasRun || size >= NoOffsetThreshold)
        {
            // skipping the offsets
            binaryReader.ReadBytes(size * 4);
        }
        for (int k = 0; k < size; ++k)
        {
            if (isBitmap[k])
            {
                containers[k] = BitmapContainer.Deserialize(binaryReader, cardinalities[k]);
            }
            else if (bitmapOfRunContainers != null && (bitmapOfRunContainers[k / 8] & (1 << (k % 8))) != 0)
            {
                ushort nbrruns = binaryReader.ReadUInt16();
                List<ushort> values = new List<ushort>(nbrruns * 2); // probably more
                int count = 0;
                bool specialCase = false;
                for (int j = 0; j < nbrruns; ++j)
                {
                    ushort value = binaryReader.ReadUInt16();
                    ushort length = binaryReader.ReadUInt16();

                    if (nbrruns == 1 && value == 0 && length == Container.MaxCapacity - 1) // special one scenario
                    {
                        containers[k] = BitmapContainer.One;
                        specialCase = true;
                        break;
                    }
                    if (nbrruns == 1 && value == 0 && length == Container.MaxSize - 1) // special one scenario
                    {
                        containers[k] = ArrayContainer.One;
                        specialCase = true;
                        break;
                    }
                    for (int i = value; i < value + length + 1; i++)
                    {
                        values.Add((ushort) i);
                    }
                    count += length;
                }
                if (!specialCase)
                {
                    if (count > Container.MaxSize)
                    {
                        containers[k] = BitmapContainer.Create(values.ToArray());
                    }
                    else
                    {
                        containers[k] = ArrayContainer.Create(values.ToArray());
                    }
                }
            }
            else
            {
                containers[k] = ArrayContainer.Deserialize(binaryReader, cardinalities[k]);
            }
        }
        for (int i = 0; i < size; i++)
        {
            if (containers[i].Equals(ArrayContainer.One))
            {
                containers[i] = ArrayContainer.One;
            }
            else if (containers[i].Equals(BitmapContainer.One))
            {
                containers[i] = BitmapContainer.One;
            }
        }
        return new RoaringArray(size, keys, containers);
    }

    public static RoaringArray Optimize(RoaringArray roaringArray)
    {
        ushort[] keys = new ushort[roaringArray._size];
        Array.Copy(roaringArray._keys, keys, roaringArray._size);
        Container[] containers = new Container[roaringArray._size];
        for (int i = 0; i < roaringArray._size; i++)
        {
            Container currentContainer = roaringArray._values[i];
            if (currentContainer.Equals(ArrayContainer.One))
            {
                containers[i] = ArrayContainer.One;
            }
            else if (currentContainer.Equals(BitmapContainer.One))
            {
                containers[i] = BitmapContainer.One;
            }
            else
            {
                containers[i] = currentContainer;
            }
        }
        return new RoaringArray(roaringArray._size, keys, containers);
    }
}
