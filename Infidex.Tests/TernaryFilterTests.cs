using Microsoft.VisualStudio.TestTools.UnitTesting;
using Infidex.Api;
using Infidex.Filtering;

namespace Infidex.Tests;

/// <summary>
/// Tests for the ternary operator (condition ? true_value : false_value)
/// </summary>
[TestClass]
public class TernaryFilterTests
{
    private FilterCompiler _compiler = null!;
    private FilterVM _vm = null!;

    [TestInitialize]
    public void Setup()
    {
        _compiler = new FilterCompiler();
        _vm = new FilterVM();
    }

    #region Basic Ternary Tests

    [TestMethod]
    public void Parse_SimpleTernary_Success()
    {
        var filter = Filter.Parse("score >= 90 ? 'high' : 'low'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(TernaryFilter));
    }

    [TestMethod]
    public void Parse_TernaryWithStrings_Success()
    {
        var filter = Filter.Parse("premium = 'yes' ? 'VIP' : 'Regular'") as TernaryFilter;
        
        Assert.IsNotNull(filter);
        Assert.IsNotNull(filter.Condition);
        Assert.IsNotNull(filter.TrueValue);
        Assert.IsNotNull(filter.FalseValue);
    }

    [TestMethod]
    public void Execute_SimpleTernary_True()
    {
        // Ternary with boolean filters as branches
        var filter = Filter.Parse("score >= 90 ? status = 'premium' : status = 'basic'");
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("score", 95);
        fields.AddField("status", "premium");

        // Since score >= 90 is true, evaluates status = 'premium', which should be true
        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    #endregion

    #region Chained Ternary Tests (Multi-way Conditionals)

    [TestMethod]
    public void Parse_ChainedTernary_Success()
    {
        var filter = Filter.Parse("score >= 90 ? 'A' : score >= 80 ? 'B' : score >= 70 ? 'C' : 'F'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(TernaryFilter));
        
        var ternary = filter as TernaryFilter;
        Assert.IsNotNull(ternary);
        
        // The false value should itself be a ternary (right-associative)
        Assert.IsInstanceOfType(ternary.FalseValue, typeof(TernaryFilter));
    }

    [TestMethod]
    public void Parse_ThreeWayTernary_Success()
    {
        var filter = Filter.Parse("level = 'high' ? 'expert' : level = 'medium' ? 'intermediate' : 'beginner'");
        
        Assert.IsNotNull(filter);
        var ternary = filter as TernaryFilter;
        Assert.IsNotNull(ternary);
        Assert.IsInstanceOfType(ternary.FalseValue, typeof(TernaryFilter));
    }

    #endregion

    #region Ternary with Boolean Operators

    [TestMethod]
    public void Parse_TernaryWithBooleanBranches_Success()
    {
        // Ternary with boolean filter branches
        var filter = Filter.Parse("premium = 'yes' ? status = 'VIP' : status = 'Regular'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(TernaryFilter));
        
        var ternary = filter as TernaryFilter;
        Assert.IsNotNull(ternary);
        Assert.IsInstanceOfType(ternary.TrueValue, typeof(ValueFilter));
        Assert.IsInstanceOfType(ternary.FalseValue, typeof(ValueFilter));
    }

    [TestMethod]
    public void Parse_ComplexConditionInTernary_Success()
    {
        var filter = Filter.Parse("(age >= 18 AND verified = 'true') ? 'approved' : 'pending'");
        
        Assert.IsNotNull(filter);
        var ternary = filter as TernaryFilter;
        Assert.IsNotNull(ternary);
        Assert.IsInstanceOfType(ternary.Condition, typeof(CompositeFilter));
    }

    [TestMethod]
    public void Parse_TernaryWithORCondition_Success()
    {
        var filter = Filter.Parse("(status = 'premium' OR status = 'vip') ? 'special' : 'regular'");
        
        Assert.IsNotNull(filter);
        var ternary = filter as TernaryFilter;
        Assert.IsNotNull(ternary);
    }

    #endregion

    #region Ternary with Parentheses

    [TestMethod]
    public void Parse_TernaryWithParentheses_Success()
    {
        var filter = Filter.Parse("(score >= 90) ? 'high' : 'low'");
        
        Assert.IsNotNull(filter);
        Assert.IsInstanceOfType(filter, typeof(TernaryFilter));
    }

    [TestMethod]
    public void Parse_NestedParenthesesInTernary_Success()
    {
        var filter = Filter.Parse("((age >= 18 AND verified = 'yes')) ? 'approved' : ((rejected = 'yes') ? 'denied' : 'pending')");
        
        Assert.IsNotNull(filter);
        var ternary = filter as TernaryFilter;
        Assert.IsNotNull(ternary);
    }

    #endregion

    #region Bytecode Compilation Tests

    [TestMethod]
    public void Compile_SimpleTernary_GeneratesBytecode()
    {
        var filter = Filter.Parse("score >= 90 ? 'high' : 'low'");
        var compiled = _compiler.Compile(filter);

        Assert.IsNotNull(compiled);
        Assert.IsNotNull(compiled.Instructions);
        Assert.IsTrue(compiled.Instructions.Length > 5); // Should have condition + branches + jumps
    }

    [TestMethod]
    public void Compile_ChainedTernary_GeneratesBytecode()
    {
        var filter = Filter.Parse("score >= 90 ? 'A' : score >= 70 ? 'B' : 'C'");
        var compiled = _compiler.Compile(filter);

        Assert.IsNotNull(compiled);
        Assert.IsTrue(compiled.Instructions.Length > 10); // Multiple conditionals
    }

    [TestMethod]
    public void Disassemble_TernaryFilter_ShowsStructure()
    {
        var filter = Filter.Parse("score >= 90 ? 'high' : 'low'");
        var compiled = _compiler.Compile(filter);
        
        string disassembly = compiled.Disassemble();
        
        Assert.IsNotNull(disassembly);
        Assert.IsTrue(disassembly.Contains("JUMP_IF_FALSE"));
        Assert.IsTrue(disassembly.Contains("JUMP"));
    }

    #endregion

    #region Precedence Tests

    [TestMethod]
    public void Parse_TernaryHasLowestPrecedence_Success()
    {
        // Ternary should have lowest precedence
        var filter = Filter.Parse("a = 'x' AND b = 'y' ? 'yes' : 'no'");
        
        Assert.IsNotNull(filter);
        var ternary = filter as TernaryFilter;
        Assert.IsNotNull(ternary);
        
        // The condition should be the AND expression
        Assert.IsInstanceOfType(ternary.Condition, typeof(CompositeFilter));
    }

    [TestMethod]
    public void Parse_TernaryWithOROperator_Success()
    {
        var filter = Filter.Parse("a = 'x' OR b = 'y' ? 'yes' : 'no'");
        
        Assert.IsNotNull(filter);
        var ternary = filter as TernaryFilter;
        Assert.IsNotNull(ternary);
        
        // The condition should be the OR expression
        Assert.IsInstanceOfType(ternary.Condition, typeof(CompositeFilter));
    }

    #endregion

    #region Right-Associativity Tests

    [TestMethod]
    public void Parse_RightAssociative_ParsesCorrectly()
    {
        // a ? b : c ? d : e  should parse as  a ? b : (c ? d : e)
        var filter = Filter.Parse("a = '1' ? 'one' : b = '2' ? 'two' : 'other'");
        
        var ternary = filter as TernaryFilter;
        Assert.IsNotNull(ternary);
        
        // The false value should be another ternary
        Assert.IsInstanceOfType(ternary.FalseValue, typeof(TernaryFilter));
        
        var innerTernary = ternary.FalseValue as TernaryFilter;
        Assert.IsNotNull(innerTernary);
    }

    #endregion

    #region Error Handling Tests

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_IncompleteTernary_ThrowsException()
    {
        // Missing : part
        Filter.Parse("score >= 90 ? 'high'");
    }

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_TernaryMissingCondition_ThrowsException()
    {
        // Missing condition
        Filter.Parse("? 'yes' : 'no'");
    }

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_TernaryMissingTrueValue_ThrowsException()
    {
        // Missing true value
        Filter.Parse("score >= 90 ? : 'low'");
    }

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_TernaryMissingFalseValue_ThrowsException()
    {
        // Missing false value
        Filter.Parse("score >= 90 ? 'high' :");
    }

    #endregion

    #region Integration Tests

    [TestMethod]
    public void Parse_TernaryWithAllOperators_Success()
    {
        var filter = Filter.Parse("score BETWEEN 80 AND 100 ? status IN ('gold', 'platinum') ? 'premium' : 'standard' : 'basic'");
        
        Assert.IsNotNull(filter);
        var ternary = filter as TernaryFilter;
        Assert.IsNotNull(ternary);
        Assert.IsInstanceOfType(ternary.Condition, typeof(RangeFilter));
    }

    [TestMethod]
    public void Parse_TernaryWithStringOperators_Success()
    {
        var filter = Filter.Parse("title CONTAINS 'magic' ? category = 'fantasy' ? 'high-fantasy' : 'fantasy' : 'other'");
        
        Assert.IsNotNull(filter);
        var ternary = filter as TernaryFilter;
        Assert.IsNotNull(ternary);
        Assert.IsInstanceOfType(ternary.Condition, typeof(StringFilter));
    }

    [TestMethod]
    public void Parse_TernaryWithNullCheck_Success()
    {
        var filter = Filter.Parse("description IS NOT NULL ? 'complete' : 'incomplete'");
        
        Assert.IsNotNull(filter);
        var ternary = filter as TernaryFilter;
        Assert.IsNotNull(ternary);
        Assert.IsInstanceOfType(ternary.Condition, typeof(NullFilter));
    }

    #endregion

    #region ToString Tests

    [TestMethod]
    public void ToString_SimpleTernary_ReturnsReadableString()
    {
        var condition = new ValueFilter("score", "high");
        var trueValue = new ValueFilter("result", "pass");
        var falseValue = new ValueFilter("result", "fail");
        var ternary = new TernaryFilter(condition, trueValue, falseValue);
        
        string result = ternary.ToString();
        
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Contains("?"));
        Assert.IsTrue(result.Contains(":"));
    }

    #endregion

    #region User Examples

    [TestMethod]
    public void Parse_UserExample_LiteralBranches_Success()
    {
        // User example: categorize based on condition with literal results
        var filter = Filter.Parse("age >= 18 ? 'adult' : 'minor'");
        
        Assert.IsNotNull(filter);
        var ternary = filter as TernaryFilter;
        Assert.IsNotNull(ternary);
        
        // Check that branches are literals
        Assert.IsInstanceOfType(ternary.TrueValue, typeof(LiteralFilter));
        Assert.IsInstanceOfType(ternary.FalseValue, typeof(LiteralFilter));
        
        var trueLiteral = ternary.TrueValue as LiteralFilter;
        var falseLiteral = ternary.FalseValue as LiteralFilter;
        
        Assert.AreEqual("adult", trueLiteral?.Value);
        Assert.AreEqual("minor", falseLiteral?.Value);
    }

    [TestMethod]
    public void Compile_LiteralBranches_Success()
    {
        // Compile ternary with literal branches
        var filter = Filter.Parse("premium = 'yes' ? 'VIP' : 'Standard'");
        var compiled = _compiler.Compile(filter);

        Assert.IsNotNull(compiled);
        Assert.IsTrue(compiled.Instructions.Length > 0);
        
        // Should contain PUSH_CONST instructions for literals
        string disassembly = compiled.Disassemble();
        Assert.IsTrue(disassembly.Contains("VIP"));
        Assert.IsTrue(disassembly.Contains("Standard"));
    }

    [TestMethod]
    public void Parse_NumericLiterals_Success()
    {
        // Ternary with numeric literals
        var filter = Filter.Parse("premium = 'yes' ? 100 : 50");
        
        Assert.IsNotNull(filter);
        var ternary = filter as TernaryFilter;
        Assert.IsNotNull(ternary);
        
        var trueLiteral = ternary.TrueValue as LiteralFilter;
        var falseLiteral = ternary.FalseValue as LiteralFilter;
        
        Assert.IsNotNull(trueLiteral);
        Assert.IsNotNull(falseLiteral);
        Assert.AreEqual(100.0, trueLiteral.Value);
        Assert.AreEqual(50.0, falseLiteral.Value);
    }

    [TestMethod]
    public void Parse_MixedLiteralsAndFilters_Success()
    {
        // Ternary with mixed branches: literal and filter
        var filter = Filter.Parse("available = 'yes' ? price >= 100 : 'unavailable'");
        
        Assert.IsNotNull(filter);
        var ternary = filter as TernaryFilter;
        Assert.IsNotNull(ternary);
        
        // True branch is a filter
        Assert.IsInstanceOfType(ternary.TrueValue, typeof(RangeFilter));
        
        // False branch is a literal
        Assert.IsInstanceOfType(ternary.FalseValue, typeof(LiteralFilter));
    }

    #endregion
}

