using Infidex.Api;

namespace Infidex.Filtering;

/// <summary>
/// Caches compiled filter operations for performance.
/// Supports boolean operations (AND, OR, NOT) on filters.
/// </summary>
public class FilterCache
{
    private readonly Dictionary<string, CompiledFilterOp> _compiledFilters;
    private readonly int _maxCacheSize;
    
    public FilterCache(int maxCacheSize = 1000)
    {
        _maxCacheSize = maxCacheSize;
        _compiledFilters = new Dictionary<string, CompiledFilterOp>();
    }
    
    /// <summary>
    /// Represents a compiled filter operation
    /// </summary>
    public class CompiledFilterOp
    {
        public FilterMask? CachedMask { get; set; }
        public FilterOp Operation { get; set; }
        
        public CompiledFilterOp(FilterOp operation)
        {
            Operation = operation;
        }
    }
    
    /// <summary>
    /// Base class for filter operations
    /// </summary>
    public abstract class FilterOp
    {
        public string HashString { get; set; } = string.Empty;
        public abstract FilterMask Apply(FilterMask? left, FilterMask? right, int docCount);
    }
    
    /// <summary>
    /// AND operation
    /// </summary>
    public class AndOp : FilterOp
    {
        public AndOp()
        {
            HashString = "AND";
        }
        
        public override FilterMask Apply(FilterMask? left, FilterMask? right, int docCount)
        {
            if (left == null || right == null)
                return new FilterMask(docCount);
            
            return left.And(right);
        }
    }
    
    /// <summary>
    /// OR operation
    /// </summary>
    public class OrOp : FilterOp
    {
        public OrOp()
        {
            HashString = "OR";
        }
        
        public override FilterMask Apply(FilterMask? left, FilterMask? right, int docCount)
        {
            if (left == null && right == null)
                return new FilterMask(docCount);
            
            if (left == null)
                return right!;
            
            if (right == null)
                return left;
            
            return left.Or(right);
        }
    }
    
    /// <summary>
    /// NOT operation
    /// </summary>
    public class NotOp : FilterOp
    {
        public NotOp()
        {
            HashString = "NOT";
        }
        
        public override FilterMask Apply(FilterMask? left, FilterMask? right, int docCount)
        {
            if (left == null)
                return new FilterMask(docCount);
            
            return left.Not();
        }
    }
    
    /// <summary>
    /// VALUE filter operation (exact match)
    /// </summary>
    public class ValueOp : FilterOp
    {
        public Filter Filter { get; }
        
        public ValueOp(Filter filter)
        {
            Filter = filter;
            HashString = $"VALUE:{filter.FieldName}={filter}";
        }
        
        public override FilterMask Apply(FilterMask? left, FilterMask? right, int docCount)
        {
            // This should be evaluated against actual documents
            // For now, return empty mask
            return new FilterMask(docCount);
        }
    }
    
    /// <summary>
    /// Compiles a filter expression into a cached operation
    /// </summary>
    public CompiledFilterOp Compile(Filter filter)
    {
        string key = filter.ToString();
        
        if (_compiledFilters.TryGetValue(key, out CompiledFilterOp? existing))
            return existing;
        
        // Create new compiled operation
        ValueOp op = new ValueOp(filter);
        CompiledFilterOp compiled = new CompiledFilterOp(op);
        
        // Add to cache (with size limit)
        if (_compiledFilters.Count < _maxCacheSize)
        {
            _compiledFilters[key] = compiled;
        }
        
        return compiled;
    }
    
    /// <summary>
    /// Evaluates a filter expression against a document set
    /// </summary>
    public FilterMask Evaluate(Filter filter, int docCount, Func<Filter, int, FilterMask> evaluator)
    {
        CompiledFilterOp compiled = Compile(filter);
        
        if (compiled.CachedMask != null)
            return compiled.CachedMask;
        
        // Evaluate and cache
        FilterMask mask = evaluator(filter, docCount);
        compiled.CachedMask = mask;
        
        return mask;
    }
    
    /// <summary>
    /// Clears the filter cache
    /// </summary>
    public void Clear()
    {
        _compiledFilters.Clear();
    }
}


