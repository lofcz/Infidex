using Microsoft.VisualStudio.TestTools.UnitTesting;
using Infidex.Api;

namespace Infidex.Tests;

/// <summary>
/// Tests for the Filter.Parse() Infiscript DSL (Domain-Specific Language).
/// Verifies that the recursive descent parser correctly handles all filter expressions.
/// 
/// Grammar reference: See Infidex/Api/Infiscript.bnf for complete BNF specification.
/// 
/// Grammar (in EBNF):
///   expression := term { 'OR' term }
///   term       := factor { 'AND' factor }
///   factor     := 'NOT' factor | '(' expression ')' | condition
///   condition  := identifier ( operator value | 'BETWEEN' value 'AND' value )
///   operator   := '=' | '!=' | '<' | '<=' | '>' | '>='
/// </summary>
[TestClass]
public class FilterParserTests
{
    #region Basic Operators
    
    [TestMethod]
    public void Parse_SimpleEquality_CreatesValueFilter()
    {
        var filter = Filter.Parse("genre = 'Fantasy'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(ValueFilter));
        
        var valueFilter = (ValueFilter)filter;
        Assert.AreEqual("genre", valueFilter.FieldName);
        Assert.AreEqual("Fantasy", valueFilter.Value);
    }
    
    [TestMethod]
    public void Parse_NotEqual_CreatesNotFilter()
    {
        var filter = Filter.Parse("status != 'inactive'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
        
        var composite = (CompositeFilter)filter;
        Assert.AreEqual(CompositeFilter.BooleanOperator.Not, composite.Operator);
    }
    
    [TestMethod]
    public void Parse_GreaterThan_CreatesRangeFilter()
    {
        var filter = Filter.Parse("price > '100'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(RangeFilter));
        
        var rangeFilter = (RangeFilter)filter;
        Assert.AreEqual("price", rangeFilter.FieldName);
        Assert.AreEqual("100", rangeFilter.MinValue);
        Assert.IsNull(rangeFilter.MaxValue);
        Assert.IsFalse(rangeFilter.IncludeMin); // > is exclusive
    }
    
    [TestMethod]
    public void Parse_GreaterThanOrEqual_CreatesRangeFilter()
    {
        var filter = Filter.Parse("year >= '2000'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(RangeFilter));
        
        var rangeFilter = (RangeFilter)filter;
        Assert.AreEqual("year", rangeFilter.FieldName);
        Assert.AreEqual("2000", rangeFilter.MinValue);
        Assert.IsNull(rangeFilter.MaxValue);
        Assert.IsTrue(rangeFilter.IncludeMin); // >= is inclusive
    }
    
    [TestMethod]
    public void Parse_LessThan_CreatesRangeFilter()
    {
        var filter = Filter.Parse("price < '500'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(RangeFilter));
        
        var rangeFilter = (RangeFilter)filter;
        Assert.AreEqual("price", rangeFilter.FieldName);
        Assert.IsNull(rangeFilter.MinValue);
        Assert.AreEqual("500", rangeFilter.MaxValue);
        Assert.IsFalse(rangeFilter.IncludeMax); // < is exclusive
    }
    
    [TestMethod]
    public void Parse_LessThanOrEqual_CreatesRangeFilter()
    {
        var filter = Filter.Parse("age <= '65'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(RangeFilter));
        
        var rangeFilter = (RangeFilter)filter;
        Assert.AreEqual("age", rangeFilter.FieldName);
        Assert.IsNull(rangeFilter.MinValue);
        Assert.AreEqual("65", rangeFilter.MaxValue);
        Assert.IsTrue(rangeFilter.IncludeMax); // <= is inclusive
    }
    
    [TestMethod]
    public void Parse_Between_CreatesRangeFilter()
    {
        var filter = Filter.Parse("price BETWEEN '10' AND '100'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(RangeFilter));
        
        var rangeFilter = (RangeFilter)filter;
        Assert.AreEqual("price", rangeFilter.FieldName);
        Assert.AreEqual("10", rangeFilter.MinValue);
        Assert.AreEqual("100", rangeFilter.MaxValue);
        Assert.IsTrue(rangeFilter.IncludeMin);
        Assert.IsTrue(rangeFilter.IncludeMax);
    }
    
    #endregion
    
    #region Boolean Operators
    
    [TestMethod]
    public void Parse_SimpleAND_CreatesCompositeFilter()
    {
        var filter = Filter.Parse("genre = 'Fantasy' AND year >= '2000'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
        
        var composite = (CompositeFilter)filter;
        Assert.AreEqual(CompositeFilter.BooleanOperator.And, composite.Operator);
        Assert.IsNotNull(composite.LeftFilter);
        Assert.IsNotNull(composite.RightFilter);
    }
    
    [TestMethod]
    public void Parse_SimpleOR_CreatesCompositeFilter()
    {
        var filter = Filter.Parse("author = 'Rowling' OR author = 'King'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
        
        var composite = (CompositeFilter)filter;
        Assert.AreEqual(CompositeFilter.BooleanOperator.Or, composite.Operator);
    }
    
    [TestMethod]
    public void Parse_NOT_CreatesCompositeFilter()
    {
        var filter = Filter.Parse("NOT status = 'inactive'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
        
        var composite = (CompositeFilter)filter;
        Assert.AreEqual(CompositeFilter.BooleanOperator.Not, composite.Operator);
        Assert.IsNotNull(composite.LeftFilter);
        Assert.IsNull(composite.RightFilter); // NOT is unary
    }
    
    [TestMethod]
    public void Parse_MultipleANDs_LeftAssociative()
    {
        // a AND b AND c should parse as ((a AND b) AND c)
        var filter = Filter.Parse("a = '1' AND b = '2' AND c = '3'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
        
        var top = (CompositeFilter)filter;
        Assert.AreEqual(CompositeFilter.BooleanOperator.And, top.Operator);
        
        // Left should be another AND
        Assert.IsInstanceOfType(top.LeftFilter, typeof(CompositeFilter));
        var left = (CompositeFilter)top.LeftFilter!;
        Assert.AreEqual(CompositeFilter.BooleanOperator.And, left.Operator);
    }
    
    [TestMethod]
    public void Parse_MultipleORs_LeftAssociative()
    {
        // a OR b OR c should parse as ((a OR b) OR c)
        var filter = Filter.Parse("a = '1' OR b = '2' OR c = '3'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
        
        var top = (CompositeFilter)filter;
        Assert.AreEqual(CompositeFilter.BooleanOperator.Or, top.Operator);
        
        // Left should be another OR
        Assert.IsInstanceOfType(top.LeftFilter, typeof(CompositeFilter));
        var left = (CompositeFilter)top.LeftFilter!;
        Assert.AreEqual(CompositeFilter.BooleanOperator.Or, left.Operator);
    }
    
    #endregion
    
    #region Operator Precedence
    
    [TestMethod]
    public void Parse_ANDBeforeOR_CorrectPrecedence()
    {
        // a OR b AND c should parse as (a OR (b AND c))
        // Because AND has higher precedence than OR
        var filter = Filter.Parse("a = '1' OR b = '2' AND c = '3'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
        
        var top = (CompositeFilter)filter;
        Assert.AreEqual(CompositeFilter.BooleanOperator.Or, top.Operator);
        
        // Right should be an AND
        Assert.IsInstanceOfType(top.RightFilter, typeof(CompositeFilter));
        var right = (CompositeFilter)top.RightFilter!;
        Assert.AreEqual(CompositeFilter.BooleanOperator.And, right.Operator);
    }
    
    [TestMethod]
    public void Parse_ParenthesesOverridePrecedence()
    {
        // (a OR b) AND c should parse as ((a OR b) AND c)
        var filter = Filter.Parse("(a = '1' OR b = '2') AND c = '3'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
        
        var top = (CompositeFilter)filter;
        Assert.AreEqual(CompositeFilter.BooleanOperator.And, top.Operator);
        
        // Left should be an OR (because of parentheses)
        Assert.IsInstanceOfType(top.LeftFilter, typeof(CompositeFilter));
        var left = (CompositeFilter)top.LeftFilter!;
        Assert.AreEqual(CompositeFilter.BooleanOperator.Or, left.Operator);
    }
    
    [TestMethod]
    public void Parse_NestedParentheses_CorrectStructure()
    {
        var filter = Filter.Parse("((a = '1' OR b = '2') AND c = '3') OR d = '4'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
        
        var top = (CompositeFilter)filter;
        Assert.AreEqual(CompositeFilter.BooleanOperator.Or, top.Operator);
    }
    
    #endregion
    
    #region Complex Expressions
    
    [TestMethod]
    public void Parse_ComplexRealWorldFilter()
    {
        var filter = Filter.Parse(
            "(genre = 'Fantasy' AND year >= '2000') OR (genre = 'Horror' AND year >= '1970')");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
        
        var top = (CompositeFilter)filter;
        Assert.AreEqual(CompositeFilter.BooleanOperator.Or, top.Operator);
        
        // Both sides should be ANDs
        Assert.IsInstanceOfType(top.LeftFilter, typeof(CompositeFilter));
        Assert.IsInstanceOfType(top.RightFilter, typeof(CompositeFilter));
    }
    
    [TestMethod]
    public void Parse_ThreeORsWithParentheses()
    {
        var filter = Filter.Parse(
            "author = 'Rowling' OR author = 'King' OR author = 'Sanderson'");
        
        Assert.IsNotNull(filter);
        // Should create left-associative tree
    }
    
    [TestMethod]
    public void Parse_NOTWithComplexExpression()
    {
        var filter = Filter.Parse("NOT (status = 'inactive' OR deleted = 'true')");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
        
        var top = (CompositeFilter)filter;
        Assert.AreEqual(CompositeFilter.BooleanOperator.Not, top.Operator);
        
        // Inner should be an OR
        Assert.IsInstanceOfType(top.LeftFilter, typeof(CompositeFilter));
        var inner = (CompositeFilter)top.LeftFilter!;
        Assert.AreEqual(CompositeFilter.BooleanOperator.Or, inner.Operator);
    }
    
    #endregion
    
    #region String Literals
    
    [TestMethod]
    public void Parse_SingleQuotes_HandledCorrectly()
    {
        var filter = Filter.Parse("name = 'John Doe'");
        
        Assert.IsNotNull(filter);
        var valueFilter = (ValueFilter)filter;
        Assert.AreEqual("John Doe", valueFilter.Value);
    }
    
    [TestMethod]
    public void Parse_DoubleQuotes_HandledCorrectly()
    {
        var filter = Filter.Parse("name = \"Jane Smith\"");
        
        Assert.IsNotNull(filter);
        var valueFilter = (ValueFilter)filter;
        Assert.AreEqual("Jane Smith", valueFilter.Value);
    }
    
    [TestMethod]
    public void Parse_StringWithSpaces_HandledCorrectly()
    {
        var filter = Filter.Parse("title = 'Harry Potter and the Philosophers Stone'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(ValueFilter));
        var valueFilter = filter as ValueFilter;
        Assert.AreEqual("Harry Potter and the Philosophers Stone", valueFilter?.Value);
    }
    
    [TestMethod]
    public void Parse_NumericValue_WithoutQuotes()
    {
        var filter = Filter.Parse("price > 100");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(RangeFilter));
        
        var rangeFilter = (RangeFilter)filter;
        Assert.AreEqual("100", rangeFilter.MinValue);
    }
    
    #endregion
    
    #region Case Insensitivity
    
    [TestMethod]
    public void Parse_AND_CaseInsensitive()
    {
        var filter1 = Filter.Parse("a = '1' AND b = '2'");
        var filter2 = Filter.Parse("a = '1' and b = '2'");
        var filter3 = Filter.Parse("a = '1' And b = '2'");
        
        Assert.IsNotNull(filter1);
        Assert.IsNotNull(filter2);
        Assert.IsNotNull(filter3);
    }
    
    [TestMethod]
    public void Parse_OR_CaseInsensitive()
    {
        var filter1 = Filter.Parse("a = '1' OR b = '2'");
        var filter2 = Filter.Parse("a = '1' or b = '2'");
        
        Assert.IsNotNull(filter1);
        Assert.IsNotNull(filter2);
    }
    
    [TestMethod]
    public void Parse_NOT_CaseInsensitive()
    {
        var filter1 = Filter.Parse("NOT a = '1'");
        var filter2 = Filter.Parse("not a = '1'");
        
        Assert.IsNotNull(filter1);
        Assert.IsNotNull(filter2);
    }
    
    [TestMethod]
    public void Parse_BETWEEN_CaseInsensitive()
    {
        var filter = Filter.Parse("price between '10' and '100'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(RangeFilter));
    }
    
    [TestMethod]
    public void Parse_IN_CaseInsensitive()
    {
        var filter1 = Filter.Parse("genre IN ('Fantasy')");
        var filter2 = Filter.Parse("genre in ('Fantasy')");
        var filter3 = Filter.Parse("genre In ('Fantasy')");
        
        Assert.IsNotNull(filter1);
        Assert.IsNotNull(filter2);
        Assert.IsNotNull(filter3);
        Assert.IsInstanceOfType(filter1, typeof(InFilter));
        Assert.IsInstanceOfType(filter2, typeof(InFilter));
        Assert.IsInstanceOfType(filter3, typeof(InFilter));
    }
    
    [TestMethod]
    public void Parse_CONTAINS_CaseInsensitive()
    {
        var filter1 = Filter.Parse("title CONTAINS 'test'");
        var filter2 = Filter.Parse("title contains 'test'");
        var filter3 = Filter.Parse("title Contains 'test'");
        
        Assert.IsNotNull(filter1);
        Assert.IsNotNull(filter2);
        Assert.IsNotNull(filter3);
    }
    
    [TestMethod]
    public void Parse_STARTS_WITH_CaseInsensitive()
    {
        var filter1 = Filter.Parse("name STARTS WITH 'John'");
        var filter2 = Filter.Parse("name starts with 'John'");
        var filter3 = Filter.Parse("name Starts With 'John'");
        
        Assert.IsNotNull(filter1);
        Assert.IsNotNull(filter2);
        Assert.IsNotNull(filter3);
    }
    
    [TestMethod]
    public void Parse_ENDS_WITH_CaseInsensitive()
    {
        var filter1 = Filter.Parse("email ENDS WITH '.com'");
        var filter2 = Filter.Parse("email ends with '.com'");
        var filter3 = Filter.Parse("email Ends With '.com'");
        
        Assert.IsNotNull(filter1);
        Assert.IsNotNull(filter2);
        Assert.IsNotNull(filter3);
    }
    
    [TestMethod]
    public void Parse_LIKE_CaseInsensitive()
    {
        var filter1 = Filter.Parse("title LIKE '%test%'");
        var filter2 = Filter.Parse("title like '%test%'");
        var filter3 = Filter.Parse("title Like '%test%'");
        
        Assert.IsNotNull(filter1);
        Assert.IsNotNull(filter2);
        Assert.IsNotNull(filter3);
    }
    
    [TestMethod]
    public void Parse_IS_NULL_CaseInsensitive()
    {
        var filter1 = Filter.Parse("description IS NULL");
        var filter2 = Filter.Parse("description is null");
        var filter3 = Filter.Parse("description Is Null");
        
        Assert.IsNotNull(filter1);
        Assert.IsNotNull(filter2);
        Assert.IsNotNull(filter3);
    }
    
    [TestMethod]
    public void Parse_IS_NOT_NULL_CaseInsensitive()
    {
        var filter1 = Filter.Parse("author IS NOT NULL");
        var filter2 = Filter.Parse("author is not null");
        var filter3 = Filter.Parse("author Is Not Null");
        
        Assert.IsNotNull(filter1);
        Assert.IsNotNull(filter2);
        Assert.IsNotNull(filter3);
    }
    
    #endregion
    
    #region Alternative Operator Syntaxes
    
    [TestMethod]
    public void Parse_AND_WithDoubleAmpersand()
    {
        var filter = Filter.Parse("genre = 'Fantasy' && year >= '2000'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
        
        var composite = (CompositeFilter)filter;
        Assert.AreEqual(CompositeFilter.BooleanOperator.And, composite.Operator);
    }
    
    [TestMethod]
    public void Parse_AND_WithSingleAmpersand()
    {
        var filter = Filter.Parse("genre = 'Fantasy' & year >= '2000'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
        
        var composite = (CompositeFilter)filter;
        Assert.AreEqual(CompositeFilter.BooleanOperator.And, composite.Operator);
    }
    
    [TestMethod]
    public void Parse_OR_WithDoublePipe()
    {
        var filter = Filter.Parse("author = 'Rowling' || author = 'King'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
        
        var composite = (CompositeFilter)filter;
        Assert.AreEqual(CompositeFilter.BooleanOperator.Or, composite.Operator);
    }
    
    [TestMethod]
    public void Parse_OR_WithSinglePipe()
    {
        var filter = Filter.Parse("author = 'Rowling' | author = 'King'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
        
        var composite = (CompositeFilter)filter;
        Assert.AreEqual(CompositeFilter.BooleanOperator.Or, composite.Operator);
    }
    
    [TestMethod]
    public void Parse_NOT_WithExclamation()
    {
        var filter = Filter.Parse("! status = 'inactive'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
        
        var composite = (CompositeFilter)filter;
        Assert.AreEqual(CompositeFilter.BooleanOperator.Not, composite.Operator);
    }
    
    [TestMethod]
    public void Parse_MixedSyntaxes_AllWork()
    {
        // Mix of SQL and C-style operators
        var filter = Filter.Parse("(genre = 'Fantasy' && year >= '2000') || (genre = 'Horror' & year >= '1970')");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
    }
    
    [TestMethod]
    public void Parse_CStyleComplexExpression()
    {
        // Pure C-style syntax
        var filter = Filter.Parse("(a = '1' && b = '2') || (c = '3' && d = '4')");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
        
        var top = (CompositeFilter)filter;
        Assert.AreEqual(CompositeFilter.BooleanOperator.Or, top.Operator);
    }
    
    [TestMethod]
    public void Parse_NotEqualVsNotOperator_Distinct()
    {
        // Ensure != (not equal) is different from ! (NOT operator)
        var filter1 = Filter.Parse("status != 'inactive'"); // not-equal
        var filter2 = Filter.Parse("! status = 'inactive'"); // NOT equal
        
        // Both should create NOT filters but structured differently
        Assert.IsNotNull(filter1);
        Assert.IsNotNull(filter2);
        Assert.IsInstanceOfType(filter1, typeof(CompositeFilter));
        Assert.IsInstanceOfType(filter2, typeof(CompositeFilter));
    }
    
    #endregion
    
    #region Whitespace Handling
    
    [TestMethod]
    public void Parse_ExtraWhitespace_Ignored()
    {
        var filter = Filter.Parse("  genre   =   'Fantasy'   AND   year   >=   '2000'  ");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
    }
    
    [TestMethod]
    public void Parse_NoWhitespace_StillWorks()
    {
        var filter = Filter.Parse("genre='Fantasy'AND year>='2000'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
    }
    
    #endregion
    
    #region Error Handling
    
    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_EmptyString_ThrowsException()
    {
        Filter.Parse("");
    }
    
    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_UnterminatedString_ThrowsException()
    {
        Filter.Parse("name = 'unterminated");
    }
    
    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_MissingClosingParen_ThrowsException()
    {
        Filter.Parse("(genre = 'Fantasy'");
    }
    
    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_MissingValue_ThrowsException()
    {
        Filter.Parse("genre =");
    }
    
    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_MissingOperator_ThrowsException()
    {
        Filter.Parse("genre 'Fantasy'");
    }
    
    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_InvalidOperator_ThrowsException()
    {
        Filter.Parse("genre === 'Fantasy'");
    }
    
    #endregion
    
    #region New Operators - IN, CONTAINS, LIKE, NULL
    
    [TestMethod]
    public void Parse_IN_WithMultipleValues()
    {
        var filter = Filter.Parse("genre IN ('Fantasy', 'SciFi', 'Horror')");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(InFilter));
        
        var inFilter = (InFilter)filter;
        Assert.AreEqual("genre", inFilter.FieldName);
        Assert.AreEqual(3, inFilter.Values.Length);
        Assert.AreEqual("Fantasy", inFilter.Values[0]);
        Assert.AreEqual("SciFi", inFilter.Values[1]);
        Assert.AreEqual("Horror", inFilter.Values[2]);
    }
    
    [TestMethod]
    public void Parse_IN_WithSingleValue()
    {
        var filter = Filter.Parse("status IN ('active')");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(InFilter));
        
        var inFilter = (InFilter)filter;
        Assert.AreEqual(1, inFilter.Values.Length);
    }
    
    [TestMethod]
    public void Parse_CONTAINS()
    {
        var filter = Filter.Parse("title CONTAINS 'Harry'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(StringFilter));
        
        var stringFilter = (StringFilter)filter;
        Assert.AreEqual("title", stringFilter.FieldName);
        Assert.AreEqual(StringFilter.StringOperation.Contains, stringFilter.Operation);
        Assert.AreEqual("Harry", stringFilter.Pattern);
    }
    
    [TestMethod]
    public void Parse_STARTS_WITH()
    {
        var filter = Filter.Parse("title STARTS WITH 'The'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(StringFilter));
        
        var stringFilter = (StringFilter)filter;
        Assert.AreEqual("title", stringFilter.FieldName);
        Assert.AreEqual(StringFilter.StringOperation.StartsWith, stringFilter.Operation);
        Assert.AreEqual("The", stringFilter.Pattern);
    }
    
    [TestMethod]
    public void Parse_ENDS_WITH()
    {
        var filter = Filter.Parse("email ENDS WITH '@example.com'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(StringFilter));
        
        var stringFilter = (StringFilter)filter;
        Assert.AreEqual("email", stringFilter.FieldName);
        Assert.AreEqual(StringFilter.StringOperation.EndsWith, stringFilter.Operation);
        Assert.AreEqual("@example.com", stringFilter.Pattern);
    }
    
    [TestMethod]
    public void Parse_LIKE_WithWildcards()
    {
        var filter = Filter.Parse("title LIKE '%Potter%'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(StringFilter));
        
        var stringFilter = (StringFilter)filter;
        Assert.AreEqual("title", stringFilter.FieldName);
        Assert.AreEqual(StringFilter.StringOperation.Like, stringFilter.Operation);
        Assert.AreEqual("%Potter%", stringFilter.Pattern);
    }
    
    [TestMethod]
    public void Parse_IS_NULL()
    {
        var filter = Filter.Parse("description IS NULL");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(NullFilter));
        
        var nullFilter = (NullFilter)filter;
        Assert.AreEqual("description", nullFilter.FieldName);
        Assert.IsTrue(nullFilter.IsNull);
    }
    
    [TestMethod]
    public void Parse_IS_NOT_NULL()
    {
        var filter = Filter.Parse("author IS NOT NULL");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(NullFilter));
        
        var nullFilter = (NullFilter)filter;
        Assert.AreEqual("author", nullFilter.FieldName);
        Assert.IsFalse(nullFilter.IsNull);
    }
    
    [TestMethod]
    public void Parse_ComplexWithNewOperators()
    {
        var filter = Filter.Parse(
            "(genre IN ('Fantasy', 'Horror') AND title CONTAINS 'magic') OR author IS NOT NULL");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
    }
    
    [TestMethod]
    public void Parse_MATCHES_SimpleRegex()
    {
        var filter = Filter.Parse("email MATCHES '^[a-z]+@.*\\.com$'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(RegexFilter));
        
        var regexFilter = (RegexFilter)filter;
        Assert.AreEqual("email", regexFilter.FieldName);
        Assert.AreEqual("^[a-z]+@.*\\.com$", regexFilter.Pattern);
    }
    
    [TestMethod]
    public void Parse_MATCHES_ISBNPattern()
    {
        var filter = Filter.Parse("isbn MATCHES '^\\d{3}-\\d{10}$'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(RegexFilter));
        
        var regexFilter = (RegexFilter)filter;
        Assert.AreEqual("isbn", regexFilter.FieldName);
    }
    
    [TestMethod]
    public void Parse_MATCHES_PhonePattern()
    {
        var filter = Filter.Parse("phone MATCHES '^\\(\\d{3}\\) \\d{3}-\\d{4}$'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(RegexFilter));
    }
    
    [TestMethod]
    public void Parse_MATCHES_CaseInsensitive()
    {
        var filter1 = Filter.Parse("name MATCHES '^John'");
        var filter2 = Filter.Parse("name matches '^John'");
        var filter3 = Filter.Parse("name Matches '^John'");
        
        Assert.IsNotNull(filter1);
        Assert.IsNotNull(filter2);
        Assert.IsNotNull(filter3);
    }
    
    [TestMethod]
    public void RegexFilter_EmailPattern_MatchesCorrectly()
    {
        var filter = new RegexFilter("email", "^[a-z]+@example\\.com$");
        
        Assert.IsTrue(filter.Matches("john@example.com"));
        Assert.IsTrue(filter.Matches("jane@example.com"));
        Assert.IsFalse(filter.Matches("invalid@other.com"));
        Assert.IsFalse(filter.Matches("not-an-email"));
    }
    
    [TestMethod]
    public void RegexFilter_PhonePattern_MatchesCorrectly()
    {
        var filter = new RegexFilter("phone", "^\\d{3}-\\d{4}$");
        
        Assert.IsTrue(filter.Matches("555-1234"));
        Assert.IsTrue(filter.Matches("123-4567"));
        Assert.IsFalse(filter.Matches("5551234")); // no dash
        Assert.IsFalse(filter.Matches("555-12345")); // too many digits
    }
    
    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void RegexFilter_InvalidPattern_ThrowsException()
    {
        // Invalid regex pattern should throw ArgumentException (from Regex constructor, not from parser)
        var filter = new RegexFilter("field", "[invalid(");
    }
    
    #endregion
    
    #region Recursive Descent Validation
    
    [TestMethod]
    public void Parse_DeepNesting_HandlesRecursion()
    {
        // Test deep nesting doesn't cause stack overflow
        var filter = Filter.Parse(
            "((((a = '1') AND (b = '2')) OR (c = '3')) AND (d = '4'))");
        
        Assert.IsNotNull(filter);
    }
    
    [TestMethod]
    public void Parse_VerifyGrammarRule_ExpressionIsOROfTerms()
    {
        // expression := term { 'OR' term }
        var filter = Filter.Parse("a = '1' OR b = '2' OR c = '3'");
        
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
        var composite = (CompositeFilter)filter;
        Assert.AreEqual(CompositeFilter.BooleanOperator.Or, composite.Operator);
    }
    
    [TestMethod]
    public void Parse_VerifyGrammarRule_TermIsANDOfFactors()
    {
        // term := factor { 'AND' factor }
        var filter = Filter.Parse("a = '1' AND b = '2' AND c = '3'");
        
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
        var composite = (CompositeFilter)filter;
        Assert.AreEqual(CompositeFilter.BooleanOperator.And, composite.Operator);
    }
    
    [TestMethod]
    public void Parse_VerifyGrammarRule_FactorCanBeNOT()
    {
        // factor := 'NOT' factor | '(' expression ')' | condition
        var filter = Filter.Parse("NOT a = '1'");
        
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
        var composite = (CompositeFilter)filter;
        Assert.AreEqual(CompositeFilter.BooleanOperator.Not, composite.Operator);
    }
    
    [TestMethod]
    public void Parse_VerifyGrammarRule_FactorCanBeParenthesized()
    {
        // factor := 'NOT' factor | '(' expression ')' | condition
        var filter = Filter.Parse("(a = '1' OR b = '2')");
        
        Assert.IsInstanceOfType(filter, typeof(CompositeFilter));
        var composite = (CompositeFilter)filter;
        Assert.AreEqual(CompositeFilter.BooleanOperator.Or, composite.Operator);
    }
    
    #endregion
}

