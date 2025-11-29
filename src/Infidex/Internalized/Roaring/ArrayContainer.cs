namespace Infidex.Internalized.Roaring;

internal class ArrayContainer : Container, IEquatable<ArrayContainer>
{
    public static readonly ArrayContainer One;
    private readonly ushort[] _content;
    private readonly int _cardinality;

    static ArrayContainer()
    {
        ushort[] data = new ushort[MaxSize];
        for (ushort i = 0; i < MaxSize; i++)
        {
            data[i] = i;
        }
        One = new ArrayContainer(MaxSize, data);
    }

    private ArrayContainer(int cardinality, ushort[] data)
    {
        _content = data;
        _cardinality = cardinality;
    }

    protected internal override int Cardinality => _cardinality;

    public override int ArraySizeInBytes => _cardinality * sizeof(ushort);

    public bool Equals(ArrayContainer? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        if (ReferenceEquals(null, other))
        {
            return false;
        }
        if (_cardinality != other._cardinality)
        {
            return false;
        }
        for (int i = 0; i < _cardinality; i++)
        {
            if (_content[i] != other._content[i])
            {
                return false;
            }
        }
        return true;
    }

    internal static ArrayContainer Create(ushort[] values)
    {
        return new ArrayContainer(values.Length, values);
    }

    internal static ArrayContainer Create(BitmapContainer bc)
    {
        ushort[] data =  GC.AllocateUninitializedArray<ushort>(bc.Cardinality);
        int cardinality = bc.FillArray(data);
        ArrayContainer result = new ArrayContainer(cardinality, data);
        return result;
    }

    protected override bool EqualsInternal(Container other)
    {
        ArrayContainer? ac = other as ArrayContainer;
        return ac != null && Equals(ac);
    }

    public override void EnumerateFill(List<int> list, int key)
    {
        for (int i = 0; i < _cardinality; i++)
        {
            list.Add(key | _content[i]);
        }
    }

    public static Container operator &(ArrayContainer x, ArrayContainer y)
    {
        int desiredCapacity = Math.Min(x._cardinality, y._cardinality);
        ushort[] data = new ushort[desiredCapacity];
        int calculatedCardinality = Utils.IntersectArrays(x._content.AsSpan(0, x._cardinality), y._content.AsSpan(0, y._cardinality), data);
        return new ArrayContainer(calculatedCardinality, data);
    }

    public static ArrayContainer operator &(ArrayContainer x, BitmapContainer y)
    {
        ushort[] data = new ushort[x._content.Length];
        int c = x._cardinality;
        int pos = 0;
        for (int i = 0; i < c; i++)
        {
            ushort v = x._content[i];
            if (y.Contains(v))
            {
                data[pos++] = v;
            }
        }
        return new ArrayContainer(pos, data);
    }

    public static Container operator |(ArrayContainer x, ArrayContainer y)
    {
        int totalCardinality = x._cardinality + y._cardinality;
        if (totalCardinality > MaxSize)
        {
            ushort[] output = new ushort[totalCardinality];
            int calcCardinality = Utils.UnionArrays(x._content, x._cardinality, y._content, y._cardinality, output);
            if (calcCardinality > MaxSize)
            {
                return BitmapContainer.Create(calcCardinality, output);
            }
            return new ArrayContainer(calcCardinality, output);
        }
        int desiredCapacity = totalCardinality;
        ushort[] data = new ushort[desiredCapacity];
        int calculatedCardinality = Utils.UnionArrays(x._content, x._cardinality, y._content, y._cardinality, data);
        return new ArrayContainer(calculatedCardinality, data);
    }

    public static Container operator |(ArrayContainer x, BitmapContainer y)
    {
        return y | x;
    }

    public static Container operator ~(ArrayContainer x)
    {
        return BitmapContainer.Create(x._cardinality, x._content, true); // an arraycontainer only contains up to 4096 values, so the negation is a bitmap container
    }

    public static Container operator ^(ArrayContainer x, ArrayContainer y)
    {
        int totalCardinality = x._cardinality + y._cardinality;
        if (totalCardinality > MaxSize)
        {
            BitmapContainer bc = BitmapContainer.CreateXor(x._content, x.Cardinality, y._content, y.Cardinality);
            if (bc.Cardinality <= MaxSize)
            {
                Create(bc);
            }
        }
        int desiredCapacity = totalCardinality;
        ushort[] data = new ushort[desiredCapacity];
        int calculatedCardinality = Utils.XorArrays(x._content, x._cardinality, y._content, y._cardinality, data);
        return new ArrayContainer(calculatedCardinality, data);
    }

    public static Container operator ^(ArrayContainer x, BitmapContainer y)
    {
        return y ^ x;
    }

    public static Container AndNot(ArrayContainer x, ArrayContainer y)
    {
        int desiredCapacity = x._cardinality;
        ushort[] data = new ushort[desiredCapacity];
        int calculatedCardinality = Utils.DifferenceArrays(x._content, x._cardinality, y._content, y._cardinality, data);
        return new ArrayContainer(calculatedCardinality, data);
    }

    public static Container AndNot(ArrayContainer x, BitmapContainer y)
    {
        ushort[] data = new ushort[x._content.Length];
        int c = x._cardinality;
        int pos = 0;
        for (int i = 0; i < c; i++)
        {
            ushort v = x._content[i];
            if (!y.Contains(v))
            {
                data[pos++] = v;
            }
        }
        return new ArrayContainer(pos, data);
    }

    public int OrArray(ulong[] bitmap)
    {
        int extraCardinality = 0;
        int yC = _cardinality;
        for (int i = 0; i < yC; i++)
        {
            ushort yValue = _content[i];
            int index = yValue >> 6;
            ulong previous = bitmap[index];
            ulong after = previous | (1UL << yValue);
            bitmap[index] = after;
            extraCardinality += (int) ((previous - after) >> 63);
        }
        return extraCardinality;
    }

    public int XorArray(ulong[] bitmap)
    {
        int extraCardinality = 0;
        int yC = _cardinality;
        for (int i = 0; i < yC; i++)
        {
            ushort yValue = _content[i];
            int index = yValue >> 6;
            ulong previous = bitmap[index];
            ulong mask = 1UL << yValue;
            bitmap[index] = previous ^ mask;
            extraCardinality += (int) (1 - 2 * ((previous & mask) >> yValue));
        }
        return extraCardinality;
    }


    public int AndNotArray(ulong[] bitmap)
    {
        int extraCardinality = 0;
        int yC = _cardinality;
        for (int i = 0; i < yC; i++)
        {
            ushort yValue = _content[i];
            int index = yValue >> 6;
            ulong previous = bitmap[index];
            ulong after = previous & ~(1UL << yValue);
            bitmap[index] = after;
            extraCardinality -= (int) ((previous ^ after) >> yValue);
        }
        return extraCardinality;
    }

    public override bool Equals(object? obj)
    {
        ArrayContainer? ac = obj as ArrayContainer;
        return ac != null && Equals(ac);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int code = 17;
            code = code * 23 + _cardinality;
            for (int i = 0; i < _cardinality; i++)
            {
                code = code * 23 + _content[i];
            }
            return code;
        }
    }

    public static void Serialize(ArrayContainer ac, BinaryWriter binaryWriter)
    {
        for (int i = 0; i < ac._cardinality; i++)
        {
            binaryWriter.Write(ac._content[i]);
        }
    }

    public static ArrayContainer Deserialize(BinaryReader binaryReader, int cardinality)
    {
        ushort[] data = new ushort[cardinality];
        for (int i = 0; i < cardinality; i++)
        {
            data[i] = binaryReader.ReadUInt16();
        }
        return new ArrayContainer(cardinality, data);
    }
}
