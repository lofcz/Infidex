using Infidex.Api;

namespace Infidex.Filtering;

/// <summary>
/// Compiles Filter objects into bytecode for execution by FilterVM
/// </summary>
public class FilterCompiler
{
    private ConstantPool _constantPool;
    private List<Instruction> _instructions;
    
    public FilterCompiler()
    {
        _constantPool = new ConstantPool();
        _instructions = new List<Instruction>();
    }
    
    /// <summary>
    /// Compile a filter to bytecode
    /// </summary>
    public CompiledFilter Compile(Filter filter)
    {
        _constantPool = new ConstantPool();
        _instructions = new List<Instruction>();
        
        CompileFilter(filter);
        
        // Add HALT instruction at the end
        _instructions.Add(new Instruction(Opcode.HALT));
        
        return new CompiledFilter(_constantPool, _instructions.ToArray());
    }
    
    private void CompileFilter(Filter filter)
    {
        switch (filter)
        {
            case CompositeFilter composite:
                CompileComposite(composite);
                break;
                
            case ValueFilter value:
                CompileValue(value);
                break;
                
            case RangeFilter range:
                CompileRange(range);
                break;
                
            case InFilter inFilter:
                CompileIn(inFilter);
                break;
                
            case StringFilter stringFilter:
                CompileString(stringFilter);
                break;
                
            case RegexFilter regexFilter:
                CompileRegex(regexFilter);
                break;
                
            case NullFilter nullFilter:
                CompileNull(nullFilter);
                break;
                
            case TernaryFilter ternaryFilter:
                CompileTernary(ternaryFilter);
                break;
                
            case LiteralFilter literalFilter:
                CompileLiteral(literalFilter);
                break;
                
            case DerivedFilter derivedFilter:
                throw new NotSupportedException("DerivedFilter (custom predicates) cannot be compiled to bytecode");
                
            default:
                throw new InvalidOperationException($"Unknown filter type: {filter.GetType().Name}");
        }
    }
    
    private void CompileComposite(CompositeFilter filter)
    {
        switch (filter.Operator)
        {
            case CompositeFilter.BooleanOperator.And:
                // AND: evaluate left, duplicate it, if false jump to end, otherwise pop and evaluate right
                CompileFilter(filter.LeftFilter!);
                _instructions.Add(new Instruction(Opcode.DUP)); // Duplicate result
                
                int andJumpPos = _instructions.Count;
                _instructions.Add(new Instruction(Opcode.JUMP_IF_FALSE, 0)); // If left is false, skip right
                
                _instructions.Add(new Instruction(Opcode.POP)); // Pop the duplicate
                CompileFilter(filter.RightFilter!); // Evaluate right (result will be on stack)
                
                // Update jump target to skip right evaluation
                int andTarget = _instructions.Count;
                _instructions[andJumpPos] = new Instruction(Opcode.JUMP_IF_FALSE, andTarget);
                break;
                
            case CompositeFilter.BooleanOperator.Or:
                // OR: evaluate left, duplicate it, if true jump to end, otherwise pop and evaluate right
                CompileFilter(filter.LeftFilter!);
                _instructions.Add(new Instruction(Opcode.DUP)); // Duplicate result
                
                int orJumpPos = _instructions.Count;
                _instructions.Add(new Instruction(Opcode.JUMP_IF_TRUE, 0)); // If left is true, skip right
                
                _instructions.Add(new Instruction(Opcode.POP)); // Pop the duplicate
                CompileFilter(filter.RightFilter!); // Evaluate right (result will be on stack)
                
                // Update jump target to skip right evaluation
                int orTarget = _instructions.Count;
                _instructions[orJumpPos] = new Instruction(Opcode.JUMP_IF_TRUE, orTarget);
                break;
                
            case CompositeFilter.BooleanOperator.Not:
                CompileFilter(filter.LeftFilter!);
                _instructions.Add(new Instruction(Opcode.NOT));
                break;
                
            default:
                throw new InvalidOperationException($"Unknown boolean operator: {filter.Operator}");
        }
    }
    
    private void CompileValue(ValueFilter filter)
    {
        // Push field value and constant, then compare
        int fieldIndex = _constantPool.AddString(filter.FieldName);
        int valueIndex = _constantPool.AddString(filter.Value?.ToString() ?? string.Empty);
        
        _instructions.Add(new Instruction(Opcode.PUSH_FIELD, fieldIndex));
        _instructions.Add(new Instruction(Opcode.PUSH_CONST, valueIndex));
        _instructions.Add(new Instruction(Opcode.EQ));
    }
    
    private void CompileRange(RangeFilter filter)
    {
        int fieldIndex = _constantPool.AddString(filter.FieldName);
        
        if (filter.MinValue != null && filter.MaxValue != null)
        {
            // BETWEEN: min <= value <= max
            int minIndex = _constantPool.AddString(filter.MinValue.ToString() ?? string.Empty);
            int maxIndex = _constantPool.AddString(filter.MaxValue.ToString() ?? string.Empty);
            
            _instructions.Add(new Instruction(Opcode.PUSH_FIELD, fieldIndex));
            _instructions.Add(new Instruction(Opcode.PUSH_CONST, minIndex));
            _instructions.Add(new Instruction(Opcode.PUSH_CONST, maxIndex));
            _instructions.Add(new Instruction(Opcode.BETWEEN));
        }
        else if (filter.MinValue != null)
        {
            // value >= min or value > min
            int minIndex = _constantPool.AddString(filter.MinValue.ToString() ?? string.Empty);
            
            _instructions.Add(new Instruction(Opcode.PUSH_FIELD, fieldIndex));
            _instructions.Add(new Instruction(Opcode.PUSH_CONST, minIndex));
            _instructions.Add(new Instruction(filter.IncludeMin ? Opcode.GTE : Opcode.GT));
        }
        else if (filter.MaxValue != null)
        {
            // value <= max or value < max
            int maxIndex = _constantPool.AddString(filter.MaxValue.ToString() ?? string.Empty);
            
            _instructions.Add(new Instruction(Opcode.PUSH_FIELD, fieldIndex));
            _instructions.Add(new Instruction(Opcode.PUSH_CONST, maxIndex));
            _instructions.Add(new Instruction(filter.IncludeMax ? Opcode.LTE : Opcode.LT));
        }
    }
    
    private void CompileIn(InFilter filter)
    {
        int fieldIndex = _constantPool.AddString(filter.FieldName);
        int arrayIndex = _constantPool.AddArray(filter.Values);
        
        _instructions.Add(new Instruction(Opcode.PUSH_FIELD, fieldIndex));
        _instructions.Add(new Instruction(Opcode.PUSH_CONST, arrayIndex));
        _instructions.Add(new Instruction(Opcode.IN));
    }
    
    private void CompileString(StringFilter filter)
    {
        int fieldIndex = _constantPool.AddString(filter.FieldName);
        int patternIndex = _constantPool.AddString(filter.Pattern);
        
        _instructions.Add(new Instruction(Opcode.PUSH_FIELD, fieldIndex));
        _instructions.Add(new Instruction(Opcode.PUSH_CONST, patternIndex));
        
        Opcode opcode = filter.Operation switch
        {
            StringFilter.StringOperation.Contains => Opcode.CONTAINS,
            StringFilter.StringOperation.StartsWith => Opcode.STARTS_WITH,
            StringFilter.StringOperation.EndsWith => Opcode.ENDS_WITH,
            StringFilter.StringOperation.Like => Opcode.LIKE,
            _ => throw new InvalidOperationException($"Unknown string operation: {filter.Operation}")
        };
        
        _instructions.Add(new Instruction(opcode));
    }
    
    private void CompileRegex(RegexFilter filter)
    {
        int fieldIndex = _constantPool.AddString(filter.FieldName);
        int patternIndex = _constantPool.AddString(filter.Pattern);
        
        _instructions.Add(new Instruction(Opcode.PUSH_FIELD, fieldIndex));
        _instructions.Add(new Instruction(Opcode.PUSH_CONST, patternIndex));
        _instructions.Add(new Instruction(Opcode.MATCHES));
    }
    
    private void CompileNull(NullFilter filter)
    {
        int fieldIndex = _constantPool.AddString(filter.FieldName);
        
        _instructions.Add(new Instruction(Opcode.PUSH_FIELD, fieldIndex));
        _instructions.Add(new Instruction(filter.IsNull ? Opcode.IS_NULL : Opcode.IS_NOT_NULL));
    }
    
    private void CompileTernary(TernaryFilter filter)
    {
        // Compile the condition
        CompileFilter(filter.Condition);
        
        // If condition is false, jump to false branch
        int falseBranchJumpPos = _instructions.Count;
        _instructions.Add(new Instruction(Opcode.JUMP_IF_FALSE, 0)); // placeholder
        
        // Compile true branch
        _instructions.Add(new Instruction(Opcode.POP)); // Pop the condition result
        CompileFilter(filter.TrueValue);
        
        // Jump to end (skip false branch)
        int endJumpPos = _instructions.Count;
        _instructions.Add(new Instruction(Opcode.JUMP, 0)); // placeholder
        
        // False branch starts here
        int falseBranchTarget = _instructions.Count;
        _instructions[falseBranchJumpPos] = new Instruction(Opcode.JUMP_IF_FALSE, falseBranchTarget);
        
        // Compile false branch
        _instructions.Add(new Instruction(Opcode.POP)); // Pop the condition result
        CompileFilter(filter.FalseValue);
        
        // End of ternary
        int endTarget = _instructions.Count;
        _instructions[endJumpPos] = new Instruction(Opcode.JUMP, endTarget);
    }
    
    private void CompileLiteral(LiteralFilter filter)
    {
        // Push the literal value onto the stack as a constant
        int constIndex;
        
        if (filter.Value is string strValue)
        {
            constIndex = _constantPool.AddString(strValue);
        }
        else if (filter.Value is double || filter.Value is int || filter.Value is long || filter.Value is decimal || filter.Value is float)
        {
            constIndex = _constantPool.AddNumber(Convert.ToDouble(filter.Value));
        }
        else if (filter.Value == null)
        {
            constIndex = _constantPool.AddString("null");
        }
        else
        {
            // Fallback: convert to string
            constIndex = _constantPool.AddString(filter.Value.ToString() ?? "null");
        }
        
        _instructions.Add(new Instruction(Opcode.PUSH_CONST, constIndex));
    }
}

/// <summary>
/// Represents a compiled filter ready for execution
/// </summary>
public class CompiledFilter
{
    public ConstantPool Constants { get; }
    public Instruction[] Instructions { get; }
    
    public CompiledFilter(ConstantPool constants, Instruction[] instructions)
    {
        Constants = constants;
        Instructions = instructions;
    }
    
    /// <summary>
    /// Get a disassembly of the bytecode for debugging
    /// </summary>
    public string Disassemble()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== Constant Pool ===");
        for (int i = 0; i < Constants.Count; i++)
        {
            var constant = Constants.Get(i);
            if (constant is object[] arr)
            {
                sb.AppendLine($"  [{i}] Array: [{string.Join(", ", arr)}]");
            }
            else
            {
                sb.AppendLine($"  [{i}] {constant}");
            }
        }
        
        sb.AppendLine();
        sb.AppendLine("=== Instructions ===");
        for (int i = 0; i < Instructions.Length; i++)
        {
            sb.AppendLine($"  {i:D4}: {Instructions[i]}");
        }
        
        return sb.ToString();
    }
}

