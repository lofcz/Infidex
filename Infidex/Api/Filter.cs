using Infidex.Filtering;

namespace Infidex.Api;

/// <summary>
/// Base class for document filters
/// </summary>
public abstract class Filter
{
    public string FieldName { get; set; }
    
    /// <summary>
    /// Gets the number of documents that match this filter.
    /// This property is computed by executing the filter against all documents.
    /// Cache this value if you need it multiple times to avoid re-evaluation.
    /// </summary>
    public int NumberOfDocumentsInFilter { get; internal set; }
    
    internal Filter(string fieldName)
    {
        FieldName = fieldName;
    }
    
    public abstract bool Matches(object? fieldValue);
    
    /// <summary>
    /// Compiles this filter to bytecode for optimized execution.
    /// The compiled bytecode can be cached and reused for better performance.
    /// </summary>
    /// <returns>A compiled filter ready for execution by the FilterVM</returns>
    public CompiledFilter Compile()
    {
        var compiler = new FilterCompiler();
        return compiler.Compile(this);
    }
    
    /// <summary>
    /// Compiles this filter and serializes it to bytecode bytes with INFISCRIPT-V1 format.
    /// The bytecode can be saved to disk or transmitted over network.
    /// </summary>
    /// <returns>Bytecode representation of the compiled filter</returns>
    public byte[] CompileToBytes()
    {
        var compiled = Compile();
        var serializer = new BytecodeSerializer();
        return serializer.Serialize(compiled);
    }
    
    /// <summary>
    /// Deserializes bytecode and returns a compiled filter ready for execution.
    /// </summary>
    /// <param name="bytecode">Bytecode in INFISCRIPT-V1 format</param>
    /// <returns>A compiled filter ready for execution</returns>
    public static CompiledFilter FromBytecode(byte[] bytecode)
    {
        var serializer = new BytecodeSerializer();
        return serializer.Deserialize(bytecode);
    }
    
    /// <summary>
    /// Parses a filter expression string into a Filter object using SQL-like WHERE clause syntax.
    /// The complete grammar specification is available in FilterGrammar.bnf.
    /// </summary>
    /// <param name="filterExpression">The filter expression string to parse</param>
    /// <returns>A Filter object representing the parsed expression</returns>
    /// <remarks>
    /// <para><strong>Supported Operators:</strong></para>
    /// <list type="bullet">
    ///   <item><description>Comparison: =, !=, &lt;, &lt;=, &gt;, &gt;=</description></item>
    ///   <item><description>Range: BETWEEN min AND max</description></item>
    ///   <item><description>List: IN (value1, value2, ...)</description></item>
    ///   <item><description>String: CONTAINS, STARTS WITH, ENDS WITH, LIKE (% wildcard)</description></item>
    ///   <item><description>Regex: MATCHES 'pattern'</description></item>
    ///   <item><description>Null checks: IS NULL, IS NOT NULL</description></item>
    /// </list>
    /// <para><strong>Boolean Operators (case-insensitive):</strong></para>
    /// <list type="bullet">
    ///   <item><description>AND (or &amp;&amp; or &amp;) - higher precedence</description></item>
    ///   <item><description>OR (or || or |) - lower precedence</description></item>
    ///   <item><description>NOT (or !) - highest precedence</description></item>
    /// </list>
    /// <para>Use parentheses for explicit grouping. All operators are case-insensitive.</para>
    /// <para>Grammar reference: See Infidex/Api/FilterGrammar.bnf for complete BNF specification.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Simple comparison
    /// var filter1 = Filter.Parse("genre = 'Fantasy'");
    /// 
    /// // Boolean logic
    /// var filter2 = Filter.Parse("genre = 'Fantasy' AND year >= 2000");
    /// 
    /// // Complex expression with grouping
    /// var filter3 = Filter.Parse("(genre = 'Fantasy' AND year >= 2000) OR (genre = 'Horror' AND year >= 1980)");
    /// 
    /// // String operations
    /// var filter4 = Filter.Parse("title CONTAINS 'magic'");
    /// var filter5 = Filter.Parse("title STARTS WITH 'The'");
    /// 
    /// // List membership
    /// var filter6 = Filter.Parse("author IN ('Rowling', 'Sanderson', 'Tolkien')");
    /// 
    /// // Range check
    /// var filter7 = Filter.Parse("year BETWEEN 2000 AND 2020");
    /// 
    /// // Regex matching
    /// var filter8 = Filter.Parse("email MATCHES '^[\\w\\.-]+@[\\w\\.-]+\\.\\w+$'");
    /// 
    /// // Alternative boolean syntax
    /// var filter9 = Filter.Parse("(genre = 'Fantasy' &amp;&amp; year >= 2000) || genre = 'Horror'");
    /// </code>
    /// </example>
    public static Filter Parse(string filterExpression)
    {
        return FilterParser.Parse(filterExpression);
    }
}


