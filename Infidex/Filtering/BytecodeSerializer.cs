using System.Text;

namespace Infidex.Filtering;

/// <summary>
/// Serializes and deserializes compiled filter bytecode with INFISCRIPT-V1 format
/// </summary>
public class BytecodeSerializer
{
    private const string MAGIC_HEADER = "INFISCRIPT-V1";
    private const ushort VERSION = 1;
    
    /// <summary>
    /// Serialize compiled filter to binary format
    /// </summary>
    public byte[] Serialize(CompiledFilter filter)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8);
        
        // Write magic header
        writer.Write(Encoding.ASCII.GetBytes(MAGIC_HEADER));
        
        // Write version
        writer.Write(VERSION);
        
        // Serialize constant pool
        byte[] constantPoolData = filter.Constants.Serialize();
        writer.Write(constantPoolData.Length);
        writer.Write(constantPoolData);
        
        // Serialize instructions
        writer.Write(filter.Instructions.Length);
        foreach (var instruction in filter.Instructions)
        {
            writer.Write((byte)instruction.Opcode);
            
            // Only write operands if needed
            if (instruction.Opcode.RequiresOperand())
            {
                writer.Write(instruction.Operand1);
                
                // Write Operand2 if it's non-zero
                if (instruction.Operand2 != 0)
                {
                    writer.Write(instruction.Operand2);
                }
            }
        }
        
        return ms.ToArray();
    }
    
    /// <summary>
    /// Deserialize binary format to compiled filter
    /// </summary>
    public CompiledFilter Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms, Encoding.UTF8);
        
        // Read and validate magic header
        byte[] magicBytes = reader.ReadBytes(MAGIC_HEADER.Length);
        string magic = Encoding.ASCII.GetString(magicBytes);
        if (magic != MAGIC_HEADER)
        {
            throw new InvalidDataException($"Invalid magic header: expected '{MAGIC_HEADER}', got '{magic}'");
        }
        
        // Read and validate version
        ushort version = reader.ReadUInt16();
        if (version != VERSION)
        {
            throw new InvalidDataException($"Unsupported version: {version}, expected {VERSION}");
        }
        
        // Deserialize constant pool
        int constantPoolSize = reader.ReadInt32();
        byte[] constantPoolData = reader.ReadBytes(constantPoolSize);
        var constantPool = ConstantPool.Deserialize(constantPoolData);
        
        // Deserialize instructions
        int instructionCount = reader.ReadInt32();
        var instructions = new Instruction[instructionCount];
        
        for (int i = 0; i < instructionCount; i++)
        {
            var opcode = (Opcode)reader.ReadByte();
            int operand1 = 0;
            int operand2 = 0;
            
            if (opcode.RequiresOperand())
            {
                operand1 = reader.ReadInt32();
                
                // Check if there's a second operand (optional)
                if (reader.BaseStream.Position < reader.BaseStream.Length &&
                    (i < instructionCount - 1 || reader.PeekChar() != -1))
                {
                    // Peek ahead to see if next byte is an opcode
                    long currentPos = reader.BaseStream.Position;
                    byte nextByte = reader.ReadByte();
                    reader.BaseStream.Position = currentPos;
                    
                    // If it's not a valid opcode, it might be operand2
                    if (!Enum.IsDefined(typeof(Opcode), nextByte) && operand2 == 0)
                    {
                        operand2 = reader.ReadInt32();
                    }
                }
            }
            
            instructions[i] = new Instruction(opcode, operand1, operand2);
        }
        
        return new CompiledFilter(constantPool, instructions);
    }
    
    /// <summary>
    /// Save compiled filter to file
    /// </summary>
    public void SaveToFile(CompiledFilter filter, string path)
    {
        byte[] data = Serialize(filter);
        File.WriteAllBytes(path, data);
    }
    
    /// <summary>
    /// Load compiled filter from file
    /// </summary>
    public CompiledFilter LoadFromFile(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        return Deserialize(data);
    }
    
    /// <summary>
    /// Validate that bytecode has correct magic header
    /// </summary>
    public static bool IsValidBytecode(byte[] data)
    {
        if (data.Length < MAGIC_HEADER.Length + 2)
            return false;
        
        string magic = Encoding.ASCII.GetString(data, 0, MAGIC_HEADER.Length);
        return magic == MAGIC_HEADER;
    }
    
    /// <summary>
    /// Get version from bytecode
    /// </summary>
    public static ushort GetVersion(byte[] data)
    {
        if (data.Length < MAGIC_HEADER.Length + 2)
            throw new InvalidDataException("Data too short to contain version");
        
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);
        
        reader.ReadBytes(MAGIC_HEADER.Length); // Skip magic header
        return reader.ReadUInt16();
    }
}

