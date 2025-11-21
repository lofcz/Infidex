using System.Text;

namespace Infidex.Filtering;

/// <summary>
/// Constant pool for storing values referenced by bytecode instructions.
/// Supports strings, numbers, and arrays.
/// </summary>
public class ConstantPool
{
    private readonly List<object> _constants;
    private readonly Dictionary<object, int> _constantIndex; // For deduplication
    
    public int Count => _constants.Count;
    
    public ConstantPool()
    {
        _constants = new List<object>();
        _constantIndex = new Dictionary<object, int>();
    }
    
    /// <summary>
    /// Add a string constant, returns index
    /// </summary>
    public int AddString(string value)
    {
        if (_constantIndex.TryGetValue(value, out int existingIndex))
            return existingIndex;
        
        int index = _constants.Count;
        _constants.Add(value);
        _constantIndex[value] = index;
        return index;
    }
    
    /// <summary>
    /// Add a numeric constant, returns index
    /// </summary>
    public int AddNumber(double value)
    {
        if (_constantIndex.TryGetValue(value, out int existingIndex))
            return existingIndex;
        
        int index = _constants.Count;
        _constants.Add(value);
        _constantIndex[value] = index;
        return index;
    }
    
    /// <summary>
    /// Add an array constant, returns index
    /// </summary>
    public int AddArray(object[] values)
    {
        int index = _constants.Count;
        _constants.Add(values);
        // Note: Arrays are not deduplicated as equality comparison is complex
        return index;
    }
    
    /// <summary>
    /// Get constant by index
    /// </summary>
    public object Get(int index)
    {
        if (index < 0 || index >= _constants.Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"Invalid constant pool index: {index}");
        
        return _constants[index];
    }
    
    /// <summary>
    /// Serialize constant pool to bytes
    /// </summary>
    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8);
        
        // Write constant count
        writer.Write(_constants.Count);
        
        // Write each constant with type tag
        foreach (var constant in _constants)
        {
            if (constant is string str)
            {
                writer.Write((byte)ConstantType.String);
                writer.Write(str);
            }
            else if (constant is double num)
            {
                writer.Write((byte)ConstantType.Number);
                writer.Write(num);
            }
            else if (constant is object[] arr)
            {
                writer.Write((byte)ConstantType.Array);
                writer.Write(arr.Length);
                foreach (var item in arr)
                {
                    // For simplicity, array elements are strings
                    writer.Write(item?.ToString() ?? string.Empty);
                }
            }
            else
            {
                throw new InvalidOperationException($"Unsupported constant type: {constant?.GetType()}");
            }
        }
        
        return ms.ToArray();
    }
    
    /// <summary>
    /// Deserialize constant pool from bytes
    /// </summary>
    public static ConstantPool Deserialize(byte[] data)
    {
        var pool = new ConstantPool();
        
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms, Encoding.UTF8);
        
        int count = reader.ReadInt32();
        
        for (int i = 0; i < count; i++)
        {
            var type = (ConstantType)reader.ReadByte();
            
            switch (type)
            {
                case ConstantType.String:
                    string str = reader.ReadString();
                    pool._constants.Add(str);
                    pool._constantIndex[str] = i;
                    break;
                    
                case ConstantType.Number:
                    double num = reader.ReadDouble();
                    pool._constants.Add(num);
                    pool._constantIndex[num] = i;
                    break;
                    
                case ConstantType.Array:
                    int arrayLength = reader.ReadInt32();
                    var array = new object[arrayLength];
                    for (int j = 0; j < arrayLength; j++)
                    {
                        array[j] = reader.ReadString();
                    }
                    pool._constants.Add(array);
                    break;
                    
                default:
                    throw new InvalidOperationException($"Unknown constant type: {type}");
            }
        }
        
        return pool;
    }
    
    private enum ConstantType : byte
    {
        String = 1,
        Number = 2,
        Array = 3
    }
}

