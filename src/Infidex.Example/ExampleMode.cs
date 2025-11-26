namespace Infidex.Example;

/// <summary>
/// Controls how the examples behave:
/// - Index: build the engine and index documents, then exit.
/// - Test: index + run predefined queries, then exit.
/// - Repl: index + run predefined queries + interactive REPL (default).
/// </summary>
public enum ExampleMode
{
    Index,
    Test,
    Repl
}


