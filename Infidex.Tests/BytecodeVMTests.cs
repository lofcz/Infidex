using Microsoft.VisualStudio.TestTools.UnitTesting;
using Infidex.Filtering;
using Infidex.Api;

namespace Infidex.Tests;

[TestClass]
public class BytecodeVMTests
{
    private FilterCompiler _compiler = null!;
    private FilterVM _vm = null!;
    private BytecodeSerializer _serializer = null!;

    [TestInitialize]
    public void Setup()
    {
        _compiler = new FilterCompiler();
        _vm = new FilterVM();
        _serializer = new BytecodeSerializer();
    }

    #region Basic Compilation Tests

    [TestMethod]
    public void Compile_SimpleValueFilter_GeneratesCorrectBytecode()
    {
        var filter = new ValueFilter("genre", "Fantasy");
        var compiled = _compiler.Compile(filter);

        Assert.IsNotNull(compiled);
        Assert.IsNotNull(compiled.Instructions);
        Assert.IsTrue(compiled.Instructions.Length > 0);
        Assert.AreEqual(Opcode.HALT, compiled.Instructions[^1].Opcode);
    }

    [TestMethod]
    public void Compile_RangeFilter_GeneratesCorrectBytecode()
    {
        var filter = new RangeFilter("year", 2000, 2020);
        var compiled = _compiler.Compile(filter);

        Assert.IsNotNull(compiled);
        Assert.IsTrue(compiled.Instructions.Length > 0);
    }

    [TestMethod]
    public void Compile_CompositeFilter_GeneratesCorrectBytecode()
    {
        var filter = new CompositeFilter(
            CompositeFilter.BooleanOperator.And,
            new ValueFilter("genre", "Fantasy"),
            new RangeFilter("year", 2000, null)
        );
        var compiled = _compiler.Compile(filter);

        Assert.IsNotNull(compiled);
        Assert.IsTrue(compiled.Instructions.Length > 0);
    }

    #endregion

    #region Value Filter Execution Tests

    [TestMethod]
    public void Execute_ValueFilter_MatchesCorrectValue()
    {
        var filter = new ValueFilter("genre", "Fantasy");
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("genre", "Fantasy");

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Execute_ValueFilter_DoesNotMatchWrongValue()
    {
        var filter = new ValueFilter("genre", "Fantasy");
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("genre", "Horror");

        bool result = _vm.Execute(compiled, fields);
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void Execute_ValueFilter_CaseInsensitiveMatch()
    {
        var filter = new ValueFilter("genre", "fantasy");
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("genre", "FANTASY");

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    #endregion

    #region Range Filter Execution Tests

    [TestMethod]
    public void Execute_RangeFilter_BetweenMinAndMax()
    {
        var filter = new RangeFilter("year", 2000, 2020);
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("year", 2010);

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Execute_RangeFilter_MinOnly()
    {
        var filter = new RangeFilter("year", 2000, null);
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("year", 2015);

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Execute_RangeFilter_MaxOnly()
    {
        var filter = new RangeFilter("year", null, 2020);
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("year", 2015);

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Execute_RangeFilter_OutOfRange()
    {
        var filter = new RangeFilter("year", 2000, 2010);
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("year", 2020);

        bool result = _vm.Execute(compiled, fields);
        Assert.IsFalse(result);
    }

    #endregion

    #region String Filter Execution Tests

    [TestMethod]
    public void Execute_StringFilter_Contains()
    {
        var filter = new StringFilter("title", StringFilter.StringOperation.Contains, "magic");
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("title", "The Magic Kingdom");

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Execute_StringFilter_StartsWith()
    {
        var filter = new StringFilter("title", StringFilter.StringOperation.StartsWith, "The");
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("title", "The Magic Kingdom");

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Execute_StringFilter_EndsWith()
    {
        var filter = new StringFilter("title", StringFilter.StringOperation.EndsWith, "Kingdom");
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("title", "The Magic Kingdom");

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Execute_StringFilter_Like()
    {
        var filter = new StringFilter("title", StringFilter.StringOperation.Like, "%Magic%");
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("title", "The Magic Kingdom");

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    #endregion

    #region Regex Filter Execution Tests

    [TestMethod]
    public void Execute_RegexFilter_ValidPattern()
    {
        var filter = new RegexFilter("email", @"^[\w\.-]+@[\w\.-]+\.\w+$");
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("email", "test@example.com");

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Execute_RegexFilter_InvalidMatch()
    {
        var filter = new RegexFilter("email", @"^[\w\.-]+@[\w\.-]+\.\w+$");
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("email", "not-an-email");

        bool result = _vm.Execute(compiled, fields);
        Assert.IsFalse(result);
    }

    #endregion

    #region IN Filter Execution Tests

    [TestMethod]
    public void Execute_InFilter_ValueInList()
    {
        var filter = new InFilter("genre", new object[] { "Fantasy", "Sci-Fi", "Horror" });
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("genre", "Fantasy");

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Execute_InFilter_ValueNotInList()
    {
        var filter = new InFilter("genre", new object[] { "Fantasy", "Sci-Fi", "Horror" });
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("genre", "Romance");

        bool result = _vm.Execute(compiled, fields);
        Assert.IsFalse(result);
    }

    #endregion

    #region Null Filter Execution Tests

    [TestMethod]
    public void Execute_NullFilter_IsNull()
    {
        var filter = new NullFilter("description", true);
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("description", null);

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Execute_NullFilter_IsNotNull()
    {
        var filter = new NullFilter("description", false);
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("description", "Some text");

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    #endregion

    #region Composite Filter Execution Tests

    [TestMethod]
    public void Execute_CompositeFilter_And_BothTrue()
    {
        var filter = new CompositeFilter(
            CompositeFilter.BooleanOperator.And,
            new ValueFilter("genre", "Fantasy"),
            new RangeFilter("year", 2000, null)
        );
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("genre", "Fantasy");
        fields.AddField("year", 2010);

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Execute_CompositeFilter_And_OneFalse()
    {
        var filter = new CompositeFilter(
            CompositeFilter.BooleanOperator.And,
            new ValueFilter("genre", "Fantasy"),
            new RangeFilter("year", 2000, null)
        );
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("genre", "Horror");
        fields.AddField("year", 2010);

        bool result = _vm.Execute(compiled, fields);
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void Execute_CompositeFilter_Or_OneTrue()
    {
        var filter = new CompositeFilter(
            CompositeFilter.BooleanOperator.Or,
            new ValueFilter("genre", "Fantasy"),
            new ValueFilter("genre", "Horror")
        );
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("genre", "Fantasy");

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Execute_CompositeFilter_Or_BothFalse()
    {
        var filter = new CompositeFilter(
            CompositeFilter.BooleanOperator.Or,
            new ValueFilter("genre", "Fantasy"),
            new ValueFilter("genre", "Horror")
        );
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("genre", "Romance");

        bool result = _vm.Execute(compiled, fields);
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void Execute_CompositeFilter_Not()
    {
        var filter = new CompositeFilter(
            CompositeFilter.BooleanOperator.Not,
            new ValueFilter("genre", "Horror")
        );
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("genre", "Fantasy");

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Execute_CompositeFilter_Complex()
    {
        // (genre = Fantasy AND year >= 2000) OR (genre = Horror AND year >= 1980)
        var filter = new CompositeFilter(
            CompositeFilter.BooleanOperator.Or,
            new CompositeFilter(
                CompositeFilter.BooleanOperator.And,
                new ValueFilter("genre", "Fantasy"),
                new RangeFilter("year", 2000, null)
            ),
            new CompositeFilter(
                CompositeFilter.BooleanOperator.And,
                new ValueFilter("genre", "Horror"),
                new RangeFilter("year", 1980, null)
            )
        );
        var compiled = _compiler.Compile(filter);

        // Test Fantasy >= 2000
        var fields1 = new DocumentFields();
        fields1.AddField("genre", "Fantasy");
        fields1.AddField("year", 2010);
        Assert.IsTrue(_vm.Execute(compiled, fields1));

        // Test Horror >= 1980
        var fields2 = new DocumentFields();
        fields2.AddField("genre", "Horror");
        fields2.AddField("year", 1990);
        Assert.IsTrue(_vm.Execute(compiled, fields2));

        // Test Romance (should fail)
        var fields3 = new DocumentFields();
        fields3.AddField("genre", "Romance");
        fields3.AddField("year", 2000);
        Assert.IsFalse(_vm.Execute(compiled, fields3));
    }

    #endregion

    #region Serialization Tests

    [TestMethod]
    public void Serialize_SimpleFilter_ValidBytecode()
    {
        var filter = new ValueFilter("genre", "Fantasy");
        var compiled = _compiler.Compile(filter);

        byte[] bytecode = _serializer.Serialize(compiled);

        Assert.IsNotNull(bytecode);
        Assert.IsTrue(bytecode.Length > 0);
        Assert.IsTrue(BytecodeSerializer.IsValidBytecode(bytecode));
    }

    [TestMethod]
    public void Serialize_HasCorrectMagicHeader()
    {
        var filter = new ValueFilter("genre", "Fantasy");
        var compiled = _compiler.Compile(filter);

        byte[] bytecode = _serializer.Serialize(compiled);

        string magic = System.Text.Encoding.ASCII.GetString(bytecode, 0, 13);
        Assert.AreEqual("INFISCRIPT-V1", magic);
    }

    [TestMethod]
    public void Deserialize_SimpleFilter_MatchesOriginal()
    {
        var filter = new ValueFilter("genre", "Fantasy");
        var compiled = _compiler.Compile(filter);

        byte[] bytecode = _serializer.Serialize(compiled);
        var deserialized = _serializer.Deserialize(bytecode);

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(compiled.Instructions.Length, deserialized.Instructions.Length);
    }

    [TestMethod]
    public void RoundTrip_ComplexFilter_ExecutesCorrectly()
    {
        var filter = new CompositeFilter(
            CompositeFilter.BooleanOperator.And,
            new ValueFilter("genre", "Fantasy"),
            new RangeFilter("year", 2000, null)
        );
        var compiled = _compiler.Compile(filter);

        // Serialize
        byte[] bytecode = _serializer.Serialize(compiled);

        // Deserialize
        var deserialized = _serializer.Deserialize(bytecode);

        // Execute and verify
        var fields = new DocumentFields();
        fields.AddField("genre", "Fantasy");
        fields.AddField("year", 2010);

        bool result = _vm.Execute(deserialized, fields);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void SaveAndLoad_Filter_ExecutesCorrectly()
    {
        var filter = new ValueFilter("genre", "Fantasy");
        var compiled = _compiler.Compile(filter);

        string tempFile = Path.GetTempFileName();
        try
        {
            // Save to file
            _serializer.SaveToFile(compiled, tempFile);

            // Load from file
            var loaded = _serializer.LoadFromFile(tempFile);

            // Execute and verify
            var fields = new DocumentFields();
            fields.AddField("genre", "Fantasy");

            bool result = _vm.Execute(loaded, fields);
            Assert.IsTrue(result);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [TestMethod]
    [ExpectedException(typeof(InvalidDataException))]
    public void Deserialize_InvalidMagicHeader_ThrowsException()
    {
        byte[] invalidBytecode = System.Text.Encoding.ASCII.GetBytes("INVALID-MAGIC");
        _serializer.Deserialize(invalidBytecode);
    }

    #endregion

    #region Disassembly Tests

    [TestMethod]
    public void Disassemble_SimpleFilter_ReturnsReadableOutput()
    {
        var filter = new ValueFilter("genre", "Fantasy");
        var compiled = _compiler.Compile(filter);

        string disassembly = compiled.Disassemble();

        Assert.IsNotNull(disassembly);
        Assert.IsTrue(disassembly.Contains("Constant Pool"));
        Assert.IsTrue(disassembly.Contains("Instructions"));
    }

    #endregion

    #region Integration Tests with FilterParser

    [TestMethod]
    public void Execute_ParsedFilter_SimpleExpression()
    {
        var filter = Filter.Parse("genre = 'Fantasy'");
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("genre", "Fantasy");

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Execute_ParsedFilter_ComplexExpression()
    {
        var filter = Filter.Parse("(genre = 'Fantasy' AND year >= 2000) OR (genre = 'Horror' AND year >= 1980)");
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("genre", "Fantasy");
        fields.AddField("year", 2010);

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Execute_ParsedFilter_InOperator()
    {
        var filter = Filter.Parse("genre IN ('Fantasy', 'Horror', 'Sci-Fi')");
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("genre", "Horror");

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Execute_ParsedFilter_StringOperators()
    {
        var filter = Filter.Parse("title CONTAINS 'magic'");
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("title", "The Magic Kingdom");

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    #endregion

    #region Performance Tests

    [TestMethod]
    public void Performance_CompiledExecution_IsFaster()
    {
        // Create a complex filter
        var filter = new CompositeFilter(
            CompositeFilter.BooleanOperator.And,
            new ValueFilter("genre", "Fantasy"),
            new RangeFilter("year", 2000, null)
        );

        // Pre-compile
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("genre", "Fantasy");
        fields.AddField("year", 2010);

        // Warm up
        for (int i = 0; i < 100; i++)
        {
            _vm.Execute(compiled, fields);
        }

        // Test compiled execution
        var sw1 = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10000; i++)
        {
            _vm.Execute(compiled, fields);
        }
        sw1.Stop();

        Console.WriteLine($"Compiled execution: {sw1.ElapsedMilliseconds}ms for 10,000 iterations");

        // Compilation should be fast enough
        Assert.IsTrue(sw1.ElapsedMilliseconds < 1000, "Execution took too long");
    }

    #endregion

    #region Convenient API Tests

    [TestMethod]
    public void Filter_Compile_ConvenientAPI()
    {
        // Create a filter and compile directly
        var filter = new ValueFilter("genre", "Fantasy");
        var compiled = filter.Compile();

        Assert.IsNotNull(compiled);
        Assert.IsTrue(compiled.Instructions.Length > 0);

        // Execute the compiled filter
        var fields = new DocumentFields();
        fields.AddField("genre", "Fantasy");

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Filter_CompileToBytes_ConvenientAPI()
    {
        // Create a filter and compile to bytes directly
        var filter = new ValueFilter("genre", "Fantasy");
        byte[] bytecode = filter.CompileToBytes();

        Assert.IsNotNull(bytecode);
        Assert.IsTrue(bytecode.Length > 0);
        Assert.IsTrue(BytecodeSerializer.IsValidBytecode(bytecode));

        // Load from bytecode
        var compiled = Filter.FromBytecode(bytecode);

        // Execute the compiled filter
        var fields = new DocumentFields();
        fields.AddField("genre", "Fantasy");

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Filter_RoundTrip_ConvenientAPI()
    {
        // Create a complex filter
        var filter = new CompositeFilter(
            CompositeFilter.BooleanOperator.And,
            new ValueFilter("genre", "Fantasy"),
            new RangeFilter("year", 2000, null)
        );

        // Compile to bytes
        byte[] bytecode = filter.CompileToBytes();

        // Save to file
        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, bytecode);

            // Load from file
            byte[] loadedBytecode = File.ReadAllBytes(tempFile);
            var compiled = Filter.FromBytecode(loadedBytecode);

            // Execute and verify
            var fields = new DocumentFields();
            fields.AddField("genre", "Fantasy");
            fields.AddField("year", 2010);

            bool result = _vm.Execute(compiled, fields);
            Assert.IsTrue(result);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [TestMethod]
    public void FilterParse_CompileToBytes_ConvenientAPI()
    {
        // Parse filter from string and compile in one go
        var filter = Filter.Parse("genre = 'Fantasy' AND year >= 2000");
        byte[] bytecode = filter.CompileToBytes();

        // Load and execute
        var compiled = Filter.FromBytecode(bytecode);

        var fields = new DocumentFields();
        fields.AddField("genre", "Fantasy");
        fields.AddField("year", 2010);

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    #endregion

    #region Edge Cases

    [TestMethod]
    public void Execute_MissingField_ReturnsFalse()
    {
        var filter = new ValueFilter("genre", "Fantasy");
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        // Don't add the genre field

        bool result = _vm.Execute(compiled, fields);
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void Execute_NullFieldValue_HandledCorrectly()
    {
        var filter = new ValueFilter("genre", "Fantasy");
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("genre", null);

        bool result = _vm.Execute(compiled, fields);
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void Execute_EmptyStringValue_HandledCorrectly()
    {
        var filter = new ValueFilter("genre", "");
        var compiled = _compiler.Compile(filter);

        var fields = new DocumentFields();
        fields.AddField("genre", "");

        bool result = _vm.Execute(compiled, fields);
        Assert.IsTrue(result);
    }

    #endregion
}

