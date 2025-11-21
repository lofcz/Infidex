using Infidex.Api;
using System.Text.RegularExpressions;

namespace Infidex.Filtering;

/// <summary>
/// Stack-based virtual machine for executing filter bytecode
/// </summary>
public class FilterVM
{
    private Stack<object?> _stack;
    private ConstantPool _constants;
    private Instruction[] _instructions;
    private int _ip; // Instruction pointer
    
    public FilterVM()
    {
        _stack = new Stack<object?>();
        _constants = new ConstantPool();
        _instructions = Array.Empty<Instruction>();
    }
    
    /// <summary>
    /// Execute compiled filter bytecode against a document
    /// </summary>
    public bool Execute(CompiledFilter filter, DocumentFields document)
    {
        _stack.Clear();
        _constants = filter.Constants;
        _instructions = filter.Instructions;
        _ip = 0;
        
        while (_ip < _instructions.Length)
        {
            var instruction = _instructions[_ip];
            ExecuteInstruction(instruction, document);
            _ip++;
        }
        
        // Result should be on top of stack
        if (_stack.Count == 0)
            return false;
        
        var result = _stack.Pop();
        return result is bool b && b;
    }
    
    private void ExecuteInstruction(Instruction inst, DocumentFields document)
    {
        switch (inst.Opcode)
        {
            case Opcode.PUSH_FIELD:
                ExecutePushField(inst.Operand1, document);
                break;
                
            case Opcode.PUSH_CONST:
                ExecutePushConst(inst.Operand1);
                break;
                
            case Opcode.POP:
                _stack.Pop();
                break;
                
            case Opcode.DUP:
                _stack.Push(_stack.Peek());
                break;
                
            // Comparison operators
            case Opcode.EQ:
                ExecuteEQ();
                break;
            case Opcode.NEQ:
                ExecuteNEQ();
                break;
            case Opcode.LT:
                ExecuteLT();
                break;
            case Opcode.LTE:
                ExecuteLTE();
                break;
            case Opcode.GT:
                ExecuteGT();
                break;
            case Opcode.GTE:
                ExecuteGTE();
                break;
                
            // Boolean operators
            case Opcode.AND:
                ExecuteAND();
                break;
            case Opcode.OR:
                ExecuteOR();
                break;
            case Opcode.NOT:
                ExecuteNOT();
                break;
                
            // String operators
            case Opcode.CONTAINS:
                ExecuteContains();
                break;
            case Opcode.STARTS_WITH:
                ExecuteStartsWith();
                break;
            case Opcode.ENDS_WITH:
                ExecuteEndsWith();
                break;
            case Opcode.LIKE:
                ExecuteLike();
                break;
            case Opcode.MATCHES:
                ExecuteMatches();
                break;
                
            // List/Range operators
            case Opcode.IN:
                ExecuteIn();
                break;
            case Opcode.BETWEEN:
                ExecuteBetween();
                break;
                
            // Null checks
            case Opcode.IS_NULL:
                ExecuteIsNull();
                break;
            case Opcode.IS_NOT_NULL:
                ExecuteIsNotNull();
                break;
                
            // Control flow
            case Opcode.JUMP:
                _ip = inst.Operand1 - 1; // -1 because _ip will be incremented
                break;
            case Opcode.JUMP_IF_FALSE:
                {
                    var value = _stack.Peek(); // Don't pop, just peek
                    if (value is bool b && !b)
                        _ip = inst.Operand1 - 1;
                }
                break;
            case Opcode.JUMP_IF_TRUE:
                {
                    var value = _stack.Peek(); // Don't pop, just peek
                    if (value is bool bt && bt)
                        _ip = inst.Operand1 - 1;
                }
                break;
                
            case Opcode.HALT:
                _ip = _instructions.Length; // Stop execution
                break;
                
            default:
                throw new InvalidOperationException($"Unknown opcode: {inst.Opcode}");
        }
    }
    
    private void ExecutePushField(int constantIndex, DocumentFields document)
    {
        string fieldName = (string)_constants.Get(constantIndex);
        var field = document.GetField(fieldName);
        _stack.Push(field?.Value);
    }
    
    private void ExecutePushConst(int constantIndex)
    {
        _stack.Push(_constants.Get(constantIndex));
    }
    
    private void ExecuteEQ()
    {
        var right = _stack.Pop();
        var left = _stack.Pop();
        _stack.Push(AreEqual(left, right));
    }
    
    private void ExecuteNEQ()
    {
        var right = _stack.Pop();
        var left = _stack.Pop();
        _stack.Push(!AreEqual(left, right));
    }
    
    private void ExecuteLT()
    {
        var right = _stack.Pop();
        var left = _stack.Pop();
        _stack.Push(CompareTo(left, right) < 0);
    }
    
    private void ExecuteLTE()
    {
        var right = _stack.Pop();
        var left = _stack.Pop();
        _stack.Push(CompareTo(left, right) <= 0);
    }
    
    private void ExecuteGT()
    {
        var right = _stack.Pop();
        var left = _stack.Pop();
        _stack.Push(CompareTo(left, right) > 0);
    }
    
    private void ExecuteGTE()
    {
        var right = _stack.Pop();
        var left = _stack.Pop();
        _stack.Push(CompareTo(left, right) >= 0);
    }
    
    private void ExecuteAND()
    {
        var right = _stack.Pop() as bool? ?? false;
        var left = _stack.Pop() as bool? ?? false;
        _stack.Push(left && right);
    }
    
    private void ExecuteOR()
    {
        var right = _stack.Pop() as bool? ?? false;
        var left = _stack.Pop() as bool? ?? false;
        _stack.Push(left || right);
    }
    
    private void ExecuteNOT()
    {
        var value = _stack.Pop() as bool? ?? false;
        _stack.Push(!value);
    }
    
    private void ExecuteContains()
    {
        var pattern = _stack.Pop()?.ToString() ?? string.Empty;
        var text = _stack.Pop()?.ToString() ?? string.Empty;
        _stack.Push(text.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }
    
    private void ExecuteStartsWith()
    {
        var pattern = _stack.Pop()?.ToString() ?? string.Empty;
        var text = _stack.Pop()?.ToString() ?? string.Empty;
        _stack.Push(text.StartsWith(pattern, StringComparison.OrdinalIgnoreCase));
    }
    
    private void ExecuteEndsWith()
    {
        var pattern = _stack.Pop()?.ToString() ?? string.Empty;
        var text = _stack.Pop()?.ToString() ?? string.Empty;
        _stack.Push(text.EndsWith(pattern, StringComparison.OrdinalIgnoreCase));
    }
    
    private void ExecuteLike()
    {
        var pattern = _stack.Pop()?.ToString() ?? string.Empty;
        var text = _stack.Pop()?.ToString() ?? string.Empty;
        
        // Convert SQL LIKE pattern to regex
        string regexPattern = "^" + Regex.Escape(pattern)
            .Replace("%", ".*")
            .Replace("_", ".") + "$";
        
        _stack.Push(Regex.IsMatch(text, regexPattern, RegexOptions.IgnoreCase));
    }
    
    private void ExecuteMatches()
    {
        var pattern = _stack.Pop()?.ToString() ?? string.Empty;
        var text = _stack.Pop()?.ToString() ?? string.Empty;
        
        try
        {
            _stack.Push(Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase));
        }
        catch (ArgumentException)
        {
            _stack.Push(false);
        }
    }
    
    private void ExecuteIn()
    {
        var array = _stack.Pop() as object[];
        var value = _stack.Pop();
        
        if (array == null)
        {
            _stack.Push(false);
            return;
        }
        
        bool found = false;
        foreach (var item in array)
        {
            if (AreEqual(value, item))
            {
                found = true;
                break;
            }
        }
        _stack.Push(found);
    }
    
    private void ExecuteBetween()
    {
        var max = _stack.Pop();
        var min = _stack.Pop();
        var value = _stack.Pop();
        
        _stack.Push(CompareTo(value, min) >= 0 && CompareTo(value, max) <= 0);
    }
    
    private void ExecuteIsNull()
    {
        var value = _stack.Pop();
        bool isNull = value == null || (value is string str && string.IsNullOrEmpty(str));
        _stack.Push(isNull);
    }
    
    private void ExecuteIsNotNull()
    {
        var value = _stack.Pop();
        bool isNull = value == null || (value is string str && string.IsNullOrEmpty(str));
        _stack.Push(!isNull);
    }
    
    private static bool AreEqual(object? left, object? right)
    {
        if (left == null && right == null) return true;
        if (left == null || right == null) return false;
        
        // Try string comparison (case insensitive for filter matching)
        string leftStr = left.ToString() ?? string.Empty;
        string rightStr = right.ToString() ?? string.Empty;
        return leftStr.Equals(rightStr, StringComparison.OrdinalIgnoreCase);
    }
    
    private static int CompareTo(object? left, object? right)
    {
        if (left == null && right == null) return 0;
        if (left == null) return -1;
        if (right == null) return 1;
        
        // Try numeric comparison first
        if (double.TryParse(left.ToString(), out double leftNum) &&
            double.TryParse(right.ToString(), out double rightNum))
        {
            return leftNum.CompareTo(rightNum);
        }
        
        // Fall back to string comparison
        string leftStr = left.ToString() ?? string.Empty;
        string rightStr = right.ToString() ?? string.Empty;
        return string.Compare(leftStr, rightStr, StringComparison.OrdinalIgnoreCase);
    }
}

