namespace Infidex.Api;

/// <summary>
/// Fluent builder for constructing complex filter expressions with arbitrary depth.
/// Supports chaining multiple filters with AND/OR/NOT operations.
/// Internal class - users should use Filter.Parse() or construct filters directly.
/// </summary>
internal class FilterBuilder
{
    private Filter? _currentFilter;
    
    private FilterBuilder(Filter? initialFilter = null)
    {
        _currentFilter = initialFilter;
    }
    
    /// <summary>
    /// Starts building a filter expression with an initial filter
    /// </summary>
    public static FilterBuilder Where(Filter filter)
    {
        return new FilterBuilder(filter);
    }
    
    /// <summary>
    /// Starts building a filter expression with a field value match
    /// </summary>
    public static FilterBuilder Where(string fieldName, object value)
    {
        return new FilterBuilder(new ValueFilter(fieldName, value));
    }
    
    /// <summary>
    /// Starts building a filter expression with a range filter
    /// </summary>
    public static FilterBuilder WhereRange(string fieldName, IComparable? min = null, IComparable? max = null)
    {
        return new FilterBuilder(new RangeFilter(fieldName, min, max));
    }
    
    /// <summary>
    /// Adds an AND condition to the filter expression
    /// </summary>
    public FilterBuilder And(Filter filter)
    {
        if (_currentFilter == null)
        {
            _currentFilter = filter;
        }
        else
        {
            _currentFilter = CompositeFilter.And(_currentFilter, filter);
        }
        return this;
    }
    
    /// <summary>
    /// Adds an AND condition with a field value match
    /// </summary>
    public FilterBuilder And(string fieldName, object value)
    {
        return And(new ValueFilter(fieldName, value));
    }
    
    /// <summary>
    /// Adds an AND condition with a range filter
    /// </summary>
    public FilterBuilder AndRange(string fieldName, IComparable? min = null, IComparable? max = null)
    {
        return And(new RangeFilter(fieldName, min, max));
    }
    
    /// <summary>
    /// Adds an OR condition to the filter expression
    /// </summary>
    public FilterBuilder Or(Filter filter)
    {
        if (_currentFilter == null)
        {
            _currentFilter = filter;
        }
        else
        {
            _currentFilter = CompositeFilter.Or(_currentFilter, filter);
        }
        return this;
    }
    
    /// <summary>
    /// Adds an OR condition with a field value match
    /// </summary>
    public FilterBuilder Or(string fieldName, object value)
    {
        return Or(new ValueFilter(fieldName, value));
    }
    
    /// <summary>
    /// Adds an OR condition with a range filter
    /// </summary>
    public FilterBuilder OrRange(string fieldName, IComparable? min = null, IComparable? max = null)
    {
        return Or(new RangeFilter(fieldName, min, max));
    }
    
    /// <summary>
    /// Negates the current filter expression
    /// </summary>
    public FilterBuilder Not()
    {
        if (_currentFilter != null)
        {
            _currentFilter = CompositeFilter.Not(_currentFilter);
        }
        return this;
    }
    
    /// <summary>
    /// Groups a sub-expression with explicit precedence
    /// Example: builder.And(FilterBuilder.Where("field1", "value1").Or("field2", "value2").Build())
    /// </summary>
    public FilterBuilder And(Func<FilterBuilder, FilterBuilder> subExpression)
    {
        var subBuilder = subExpression(new FilterBuilder());
        var subFilter = subBuilder.Build();
        if (subFilter != null)
        {
            return And(subFilter);
        }
        return this;
    }
    
    /// <summary>
    /// Groups a sub-expression with explicit precedence
    /// Example: builder.Or(FilterBuilder.Where("field1", "value1").And("field2", "value2").Build())
    /// </summary>
    public FilterBuilder Or(Func<FilterBuilder, FilterBuilder> subExpression)
    {
        var subBuilder = subExpression(new FilterBuilder());
        var subFilter = subBuilder.Build();
        if (subFilter != null)
        {
            return Or(subFilter);
        }
        return this;
    }
    
    /// <summary>
    /// Builds and returns the final filter expression
    /// </summary>
    public Filter? Build()
    {
        return _currentFilter;
    }
    
    /// <summary>
    /// Implicit conversion to Filter for convenience
    /// </summary>
    public static implicit operator Filter?(FilterBuilder builder)
    {
        return builder.Build();
    }
}

