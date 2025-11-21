namespace Infidex.Api;

/// <summary>
/// Combines multiple filters using boolean logic (AND, OR, NOT).
/// Allows building complex filter expressions.
/// </summary>
public class CompositeFilter : Filter
{
    public enum BooleanOperator
    {
        And,
        Or,
        Not
    }
    
    public BooleanOperator Operator { get; set; }
    public Filter? LeftFilter { get; set; }
    public Filter? RightFilter { get; set; }
    
    /// <summary>
    /// Creates a composite filter with AND/OR logic
    /// </summary>
    public CompositeFilter(BooleanOperator op, Filter left, Filter? right = null) 
        : base($"composite_{op}")
    {
        Operator = op;
        LeftFilter = left;
        RightFilter = right;
        
        // NOT operation only needs left filter
        if (op == BooleanOperator.Not && right != null)
        {
            throw new ArgumentException("NOT operator should only have left filter");
        }
        
        // AND/OR operations need both filters
        if ((op == BooleanOperator.And || op == BooleanOperator.Or) && right == null)
        {
            throw new ArgumentException($"{op} operator requires both left and right filters");
        }
    }
    
    /// <summary>
    /// Helper: Creates an AND composite filter
    /// </summary>
    public static CompositeFilter And(Filter left, Filter right)
    {
        return new CompositeFilter(BooleanOperator.And, left, right);
    }
    
    /// <summary>
    /// Helper: Creates an OR composite filter
    /// </summary>
    public static CompositeFilter Or(Filter left, Filter right)
    {
        return new CompositeFilter(BooleanOperator.Or, left, right);
    }
    
    /// <summary>
    /// Helper: Creates a NOT composite filter
    /// </summary>
    public static CompositeFilter Not(Filter filter)
    {
        return new CompositeFilter(BooleanOperator.Not, filter);
    }
    
    public override bool Matches(object? fieldValue)
    {
        // CompositeFilter matches against entire documents, not single field values
        // This method should not be called directly - use document-level matching instead
        throw new NotSupportedException(
            "CompositeFilter requires document-level evaluation. " +
            "Use SearchEngine filtering which evaluates all component filters.");
    }
    
    /// <summary>
    /// Evaluates the composite filter against a document's fields
    /// </summary>
    public bool MatchesDocument(DocumentFields fields)
    {
        if (LeftFilter == null)
            return false;
        
        bool leftResult = EvaluateFilter(LeftFilter, fields);
        
        switch (Operator)
        {
            case BooleanOperator.Not:
                return !leftResult;
                
            case BooleanOperator.And:
                if (RightFilter == null) return leftResult;
                bool rightResultAnd = EvaluateFilter(RightFilter, fields);
                return leftResult && rightResultAnd;
                
            case BooleanOperator.Or:
                if (RightFilter == null) return leftResult;
                bool rightResultOr = EvaluateFilter(RightFilter, fields);
                return leftResult || rightResultOr;
                
            default:
                return false;
        }
    }
    
    private bool EvaluateFilter(Filter filter, DocumentFields fields)
    {
        // Handle nested composite filters recursively
        if (filter is CompositeFilter composite)
        {
            return composite.MatchesDocument(fields);
        }
        
        // Handle simple filters
        var field = fields.GetField(filter.FieldName);
        if (field == null)
            return false;
        
        return filter.Matches(field.Value);
    }
    
    public override string ToString()
    {
        return Operator switch
        {
            BooleanOperator.Not => $"NOT ({LeftFilter})",
            BooleanOperator.And => $"({LeftFilter} AND {RightFilter})",
            BooleanOperator.Or => $"({LeftFilter} OR {RightFilter})",
            _ => "Unknown"
        };
    }
}

