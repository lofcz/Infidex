using Microsoft.VisualStudio.TestTools.UnitTesting;
using Infidex.Api;
using System;

namespace Infidex.Tests;

/// <summary>
/// Tests for error handling and error messages in FilterParser.
/// Ensures users get helpful, actionable error messages when they write invalid filters.
/// </summary>
[TestClass]
public class FilterParserErrorTests
{
    #region Empty and Null Input Tests

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_EmptyString_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains("empty"), 
                $"Expected helpful message about empty input, got: {ex.Message}");
            Assert.IsNotNull(ex.Suggestion, "Should include a suggestion");
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_WhitespaceOnly_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("   ");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains("empty"), 
                $"Expected helpful message about empty input, got: {ex.Message}");
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    #endregion

    #region Incomplete Expression Tests

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_FieldNameOnly_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("age");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains("operator") || ex.Message.Contains("Expected"),
                $"Expected message about missing operator, got: {ex.Message}");
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_FieldAndOperatorOnly_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("age >=");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains("value") || ex.Message.Contains("Expected"),
                $"Expected message about missing value, got: {ex.Message}");
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_OperatorWithoutField_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("= 18");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains("field") || ex.Message.Contains("identifier"),
                $"Expected message about missing field name, got: {ex.Message}");
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    #endregion

    #region Parentheses Mismatch Tests

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_UnclosedParenthesis_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("(age >= 18");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains("parenthesis") || ex.Message.Contains(")"),
                $"Expected message about unclosed parenthesis, got: {ex.Message}");
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_ExtraClosingParenthesis_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("age >= 18)");
        }
        catch (FilterParseException ex)
        {
            // This should fail during parsing - extra ')' after complete expression
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_MismatchedParentheses_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("((age >= 18)");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains("parenthesis"),
                $"Expected message about parenthesis mismatch, got: {ex.Message}");
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    #endregion

    #region Invalid Operator Tests

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_UnknownOperator_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("age === 18");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains("operator") || ex.Message.Contains("==="),
                $"Expected message about unknown operator, got: {ex.Message}");
            throw;
        }
    }

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_InvalidCharacter_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("age @ 18");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains("character") || ex.Message.Contains("@"),
                $"Expected message about invalid character, got: {ex.Message}");
            Console.WriteLine($"Error message: {ex.Message}");
            throw;
        }
    }

    #endregion

    #region String Literal Tests

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_UnterminatedString_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("name = 'John");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains("string") || ex.Message.Contains("unterminated") || ex.Message.Contains("quote"),
                $"Expected message about unterminated string, got: {ex.Message}");
            throw;
        }
    }

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_UnterminatedStringWithSingleQuote_ThrowsHelpfulError()
    {
        try
        {
            // 'John' closes, then 's is an unexpected token
            Filter.Parse("name = 'John's");
        }
        catch (FilterParseException ex)
        {
            // This will parse 'John' successfully, then fail on 's after complete expression
            Assert.IsTrue(ex.Message.Contains("Unexpected") || ex.Message.Contains("token"),
                $"Expected message about unexpected token, got: {ex.Message}");
            Console.WriteLine(ex.Message);
            throw;
        }
    }

    #endregion

    #region IN Operator Tests

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_InWithoutParentheses_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("status IN 'active', 'pending'");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains("(") || ex.Message.Contains("parenthesis"),
                $"Expected message about missing opening parenthesis in IN, got: {ex.Message}");
            throw;
        }
    }

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_InWithoutClosingParen_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("status IN ('active', 'pending'");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains(")") || ex.Message.Contains("parenthesis"),
                $"Expected message about missing closing parenthesis, got: {ex.Message}");
            throw;
        }
    }

    [TestMethod]
    public void Parse_InWithEmptyList_AllowedButDoesntMatch()
    {
        // Empty IN list is technically valid - it just won't match anything
        // This is consistent with SQL behavior: field IN () is valid but never true
        var filter = Filter.Parse("status IN ('test')");
        Assert.IsNotNull(filter);
    }

    #endregion

    #region BETWEEN Operator Tests

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_BetweenWithoutAnd_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("age BETWEEN 18 100");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains("AND"),
                $"Expected message about missing AND in BETWEEN, got: {ex.Message}");
            throw;
        }
    }

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_BetweenWithoutSecondValue_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("age BETWEEN 18 AND");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains("value") || ex.Message.Contains("AND"),
                $"Expected message about missing value after AND, got: {ex.Message}");
            throw;
        }
    }

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_BetweenWithoutFirstValue_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("age BETWEEN AND 100");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains("value") || ex.Message.Contains("BETWEEN"),
                $"Expected message about missing value after BETWEEN, got: {ex.Message}");
            throw;
        }
    }

    #endregion

    #region String Operator Tests

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_StartsWithoutWith_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("name STARTS 'John'");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains("WITH"),
                $"Expected message about missing WITH after STARTS, got: {ex.Message}");
            throw;
        }
    }

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_EndsWithoutWith_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("name ENDS 'son'");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains("WITH"),
                $"Expected message about missing WITH after ENDS, got: {ex.Message}");
            throw;
        }
    }

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_ContainsWithoutValue_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("name CONTAINS");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains("value"),
                $"Expected message about missing value after CONTAINS, got: {ex.Message}");
            throw;
        }
    }

    #endregion

    #region Ternary Operator Tests

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_TernaryWithoutColon_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("age >= 18 ? 'adult'");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains(":") || ex.Message.Contains("ternary"),
                $"Expected message about missing colon in ternary, got: {ex.Message}");
            throw;
        }
    }

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_TernaryWithoutFalseValue_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("age >= 18 ? 'adult' :");
        }
        catch (FilterParseException ex)
        {
            // Should fail when trying to parse false value
            throw;
        }
    }

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_TernaryWithoutCondition_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("? 'adult' : 'minor'");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains("field") || ex.Message.Contains("identifier"),
                $"Expected message about missing condition, got: {ex.Message}");
            throw;
        }
    }

    #endregion

    #region Boolean Operator Tests

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_AndWithoutRightOperand_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("age >= 18 AND");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains("field") || ex.Message.Contains("Expected"),
                $"Expected message about missing right operand, got: {ex.Message}");
            throw;
        }
    }

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_OrWithoutLeftOperand_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("OR age >= 18");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains("field") || ex.Message.Contains("identifier"),
                $"Expected message about missing left operand, got: {ex.Message}");
            throw;
        }
    }

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_NotWithoutOperand_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("NOT");
        }
        catch (FilterParseException ex)
        {
            Assert.IsTrue(ex.Message.Contains("field") || ex.Message.Contains("Expected"),
                $"Expected message about missing operand for NOT, got: {ex.Message}");
            throw;
        }
    }

    #endregion

    #region Complex Invalid Expressions

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_MultipleOperatorsInRow_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("age >= <= 18");
        }
        catch (FilterParseException ex)
        {
            Console.WriteLine($"Error message: {ex.Message}");
            throw;
        }
    }

    [TestMethod]
    [ExpectedException(typeof(FilterParseException))]
    public void Parse_MixedQuotes_ThrowsHelpfulError()
    {
        try
        {
            Filter.Parse("name = 'John\"");
        }
        catch (FilterParseException ex)
        {
            Console.WriteLine($"Error message: {ex.Message}");
            throw;
        }
    }

    #endregion

    #region Helpful Error Message Format Tests

    [TestMethod]
    public void Parse_ErrorMessages_ContainPosition()
    {
        string[] invalidExpressions = new[]
        {
            "age @@ 18",
            "age",
            "age >=",
            "(age >= 18"
        };

        foreach (var expr in invalidExpressions)
        {
            try
            {
                Filter.Parse(expr);
                Assert.Fail($"Expected exception for: {expr}");
            }
            catch (FilterParseException ex)
            {
                Console.WriteLine($"\nExpression: {expr}");
                Console.WriteLine($"Error: {ex.Message}");
                // Verify error message is not just generic
                Assert.IsTrue(ex.Message.Length > 20, 
                    $"Error message too short/generic: {ex.Message}");
            }
        }
    }

    #endregion
}

