namespace Infidex.Filtering;

/// <summary>
/// Opcodes for the stack-based filter bytecode VM
/// </summary>
public enum Opcode : byte
{
    // Stack operations
    PUSH_FIELD = 0x01,      // Push field value from document (operand: constant pool index for field name)
    PUSH_CONST = 0x02,      // Push constant value (operand: constant pool index)
    POP = 0x03,             // Pop top value from stack
    DUP = 0x04,             // Duplicate top value on stack
    
    // Comparison operators
    EQ = 0x10,              // Pop 2 values, push true if equal
    NEQ = 0x11,             // Pop 2 values, push true if not equal
    LT = 0x12,              // Pop 2 values, push true if first < second
    LTE = 0x13,             // Pop 2 values, push true if first <= second
    GT = 0x14,              // Pop 2 values, push true if first > second
    GTE = 0x15,             // Pop 2 values, push true if first >= second
    
    // Boolean operators
    AND = 0x20,             // Pop 2 bool values, push AND result
    OR = 0x21,              // Pop 2 bool values, push OR result
    NOT = 0x22,             // Pop bool value, push NOT result
    
    // String operators
    CONTAINS = 0x30,        // Pop pattern, pop string, push true if string contains pattern
    STARTS_WITH = 0x31,     // Pop pattern, pop string, push true if string starts with pattern
    ENDS_WITH = 0x32,       // Pop pattern, pop string, push true if string ends with pattern
    LIKE = 0x33,            // Pop pattern, pop string, push true if string matches SQL LIKE pattern
    MATCHES = 0x34,         // Pop regex pattern, pop string, push true if string matches regex
    
    // List/Range operators
    IN = 0x40,              // Pop array, pop value, push true if value in array
    BETWEEN = 0x41,         // Pop max, pop min, pop value, push true if min <= value <= max
    
    // Null checks
    IS_NULL = 0x50,         // Pop value, push true if null
    IS_NOT_NULL = 0x51,     // Pop value, push true if not null
    
    // Control flow
    JUMP = 0x60,            // Unconditional jump (operand: instruction offset)
    JUMP_IF_FALSE = 0x61,   // Pop bool, jump if false (operand: instruction offset)
    JUMP_IF_TRUE = 0x62,    // Pop bool, jump if true (operand: instruction offset)
    
    // Special
    HALT = 0xFF             // Stop execution, top of stack is result
}

/// <summary>
/// Represents a single bytecode instruction with optional operands
/// </summary>
public struct Instruction
{
    public Opcode Opcode { get; set; }
    public int Operand1 { get; set; }  // For constant pool index, jump offset, etc.
    public int Operand2 { get; set; }  // For additional data if needed
    
    public Instruction(Opcode opcode, int operand1 = 0, int operand2 = 0)
    {
        Opcode = opcode;
        Operand1 = operand1;
        Operand2 = operand2;
    }
    
    public override string ToString()
    {
        if (Operand1 == 0 && Operand2 == 0)
            return Opcode.ToString();
        if (Operand2 == 0)
            return $"{Opcode} {Operand1}";
        return $"{Opcode} {Operand1}, {Operand2}";
    }
}

/// <summary>
/// Helper extension methods for bytecode generation
/// </summary>
public static class InstructionExtensions
{
    public static bool RequiresOperand(this Opcode opcode)
    {
        return opcode switch
        {
            Opcode.PUSH_FIELD => true,
            Opcode.PUSH_CONST => true,
            Opcode.JUMP => true,
            Opcode.JUMP_IF_FALSE => true,
            Opcode.JUMP_IF_TRUE => true,
            _ => false
        };
    }
    
    public static int GetOperandCount(this Opcode opcode)
    {
        return opcode switch
        {
            Opcode.PUSH_FIELD => 1,
            Opcode.PUSH_CONST => 1,
            Opcode.JUMP => 1,
            Opcode.JUMP_IF_FALSE => 1,
            Opcode.JUMP_IF_TRUE => 1,
            _ => 0
        };
    }
}

