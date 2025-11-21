using System.Text;
using System.Text.RegularExpressions;

namespace Infidex.Api;

/// <summary>
/// Parses string-based filter expressions into Filter objects.
/// Supports SQL-like WHERE clause syntax for intuitive filtering.
/// 
/// Boolean operators support multiple syntaxes:
///   - AND: "AND", "and", "&&", "&"
///   - OR:  "OR", "or", "||", "|"
///   - NOT: "NOT", "not", "!"
/// 
/// Supported operators:
///   - Comparison: =, !=, <, <=, >, >=
///   - Range: BETWEEN min AND max
///   - List: IN (value1, value2, ...)
///   - String: CONTAINS, STARTS WITH, ENDS WITH, LIKE (% wildcard)
///   - Regex: MATCHES 'pattern'
///   - Null: IS NULL, IS NOT NULL
/// 
/// Examples:
///   "genre = 'Fantasy' AND year >= '2000'"
///   "genre IN ('Fantasy', 'SciFi', 'Horror')"
///   "title CONTAINS 'Harry'"
///   "title STARTS WITH 'The'"
///   "email ENDS WITH '@example.com'"
///   "title LIKE '%Potter%'"
///   "email MATCHES '^[a-z]+@.*\.com$'"
///   "isbn MATCHES '^\d{3}-\d{10}$'"
///   "description IS NOT NULL"
///   "(genre = 'Fantasy' && year >= '2000') || (author IN ('Rowling', 'King'))"
/// </summary>
internal static class FilterParser
{
    private static string _currentExpression = string.Empty;
    
    /// <summary>
    /// Parses a filter expression string into a Filter object
    /// </summary>
    public static Filter Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new FilterParseException(
                "Filter expression cannot be empty or whitespace.",
                expression ?? "",
                0,
                "Provide a valid filter expression like: field = 'value' or age >= 18");
        }
        
        _currentExpression = expression;
        var tokens = Tokenize(expression);
        int position = 0;
        var result = ParseTernaryExpression(tokens, ref position);
        
        // Check for unparsed tokens (e.g., extra closing parentheses)
        if (position < tokens.Count)
        {
            var token = tokens[position];
            throw new FilterParseException(
                $"Unexpected token '{token.Value}' after complete expression.",
                expression,
                GetCharPosition(tokens, position),
                "Check for extra closing parentheses ')' or misplaced operators.");
        }
        
        return result;
    }
    
    private static int GetCharPosition(List<Token> tokens, int tokenPos)
    {
        if (tokenPos >= tokens.Count) return _currentExpression.Length;
        // Approximate character position - we'd need to track actual positions in tokens for perfect accuracy
        int charPos = 0;
        for (int i = 0; i < tokenPos && i < tokens.Count; i++)
        {
            charPos += tokens[i].Value.Length + 1; // +1 for approximate spacing
        }
        return Math.Min(charPos, _currentExpression.Length);
    }
    
    private static Filter ParseTernaryExpression(List<Token> tokens, ref int pos)
    {
        // Parse the condition (left side)
        Filter condition = ParseExpression(tokens, ref pos);
        
        // Check for ternary operator '?'
        if (pos < tokens.Count && tokens[pos].Type == TokenType.Question)
        {
            pos++; // consume '?'
            
            // Parse true value (right-associative recursion)
            Filter trueValue = ParseTernaryExpression(tokens, ref pos);
            
            // Expect ':'
            if (pos >= tokens.Count || tokens[pos].Type != TokenType.Colon)
            {
                throw new FilterParseException(
                    "Expected ':' (colon) in ternary expression after true value.",
                    _currentExpression,
                    GetCharPosition(tokens, pos),
                    "Ternary format is: condition ? true_value : false_value");
            }
            pos++; // consume ':'
            
            // Parse false value (right-associative recursion)
            Filter falseValue = ParseTernaryExpression(tokens, ref pos);
            
            return new TernaryFilter(condition, trueValue, falseValue);
        }
        
        return condition;
    }
    
    private static Filter ParseExpression(List<Token> tokens, ref int pos)
    {
        Filter left = ParseTerm(tokens, ref pos);
        
        while (pos < tokens.Count)
        {
            if (tokens[pos].Type == TokenType.Or)
            {
                pos++; // consume OR
                Filter right = ParseTerm(tokens, ref pos);
                left = CompositeFilter.Or(left, right);
            }
            else
            {
                break;
            }
        }
        
        return left;
    }
    
    private static Filter ParseTerm(List<Token> tokens, ref int pos)
    {
        Filter left = ParseFactor(tokens, ref pos);
        
        while (pos < tokens.Count && tokens[pos].Type == TokenType.And)
        {
            pos++; // consume AND
            Filter right = ParseFactor(tokens, ref pos);
            left = CompositeFilter.And(left, right);
        }
        
        return left;
    }
    
    private static Filter ParseFactor(List<Token> tokens, ref int pos)
    {
        // Handle NOT
        if (pos < tokens.Count && tokens[pos].Type == TokenType.Not)
        {
            pos++; // consume NOT
            Filter inner = ParseFactor(tokens, ref pos);
            return CompositeFilter.Not(inner);
        }
        
        // Handle parentheses
        if (pos < tokens.Count && tokens[pos].Type == TokenType.LeftParen)
        {
            pos++; // consume (
            Filter inner = ParseTernaryExpression(tokens, ref pos);
            if (pos >= tokens.Count || tokens[pos].Type != TokenType.RightParen)
            {
                throw new FilterParseException(
                    "Expected closing parenthesis ')'.",
                    _currentExpression,
                    GetCharPosition(tokens, pos),
                    "Make sure every '(' has a matching ')'.");
            }
            pos++; // consume )
            return inner;
        }
        
        // Handle literal values (for ternary expression branches)
        if (pos < tokens.Count && tokens[pos].Type == TokenType.Value)
        {
            string value = tokens[pos].Value;
            pos++;
            
            // Try to parse as number, otherwise treat as string
            if (double.TryParse(value, out double numValue))
            {
                return new LiteralFilter(numValue);
            }
            return new LiteralFilter(value);
        }
        
        // Parse simple condition: field op value
        return ParseCondition(tokens, ref pos);
    }
    
    private static Filter ParseCondition(List<Token> tokens, ref int pos)
    {
        if (pos >= tokens.Count || tokens[pos].Type != TokenType.Identifier)
        {
            string found = pos >= tokens.Count ? "end of expression" : $"'{tokens[pos].Value}'";
            throw new FilterParseException(
                $"Expected field name, but found {found}.",
                _currentExpression,
                GetCharPosition(tokens, pos),
                "Field names must start with a letter or underscore, like: age, user_name, _id");
        }
        
        string fieldName = tokens[pos].Value;
        pos++;
        
        // Handle IN operator
        if (pos < tokens.Count && tokens[pos].Type == TokenType.In)
        {
            pos++; // consume IN
            
            if (pos >= tokens.Count || tokens[pos].Type != TokenType.LeftParen)
            {
                throw new FilterParseException(
                    "Expected '(' after IN keyword.",
                    _currentExpression,
                    GetCharPosition(tokens, pos),
                    "IN syntax: field IN ('value1', 'value2', ...)");
            }
            pos++; // consume (
            
            var values = new List<object>();
            while (pos < tokens.Count && tokens[pos].Type != TokenType.RightParen)
            {
                if (tokens[pos].Type != TokenType.Value)
                    throw new ArgumentException("Expected value in IN clause");
                values.Add(tokens[pos].Value);
                pos++;
                
                if (pos < tokens.Count && tokens[pos].Type == TokenType.Comma)
                    pos++; // consume comma
            }
            
            if (pos >= tokens.Count || tokens[pos].Type != TokenType.RightParen)
            {
                throw new FilterParseException(
                    "Expected ')' after IN clause values.",
                    _currentExpression,
                    GetCharPosition(tokens, pos),
                    "IN syntax: field IN ('value1', 'value2', ...)");
            }
            pos++; // consume )
            
            return new InFilter(fieldName, values.ToArray());
        }
        
        // Handle CONTAINS operator
        if (pos < tokens.Count && tokens[pos].Type == TokenType.Contains)
        {
            pos++; // consume CONTAINS
            
            if (pos >= tokens.Count || tokens[pos].Type != TokenType.Value)
            {
                throw new FilterParseException(
                    "Expected string value after CONTAINS.",
                    _currentExpression,
                    GetCharPosition(tokens, pos),
                    "CONTAINS syntax: field CONTAINS 'text'");
            }
            string containsValue = tokens[pos].Value;
            pos++;
            
            return new StringFilter(fieldName, StringFilter.StringOperation.Contains, containsValue);
        }
        
        // Handle STARTS WITH operator
        if (pos < tokens.Count && tokens[pos].Type == TokenType.StartsWith)
        {
            pos++; // consume STARTS
            
            if (pos >= tokens.Count || tokens[pos].Type != TokenType.With)
            {
                throw new FilterParseException(
                    "Expected WITH after STARTS keyword.",
                    _currentExpression,
                    GetCharPosition(tokens, pos),
                    "String matching syntax: field STARTS WITH 'text'");
            }
            pos++; // consume WITH
            
            if (pos >= tokens.Count || tokens[pos].Type != TokenType.Value)
            {
                throw new FilterParseException(
                    "Expected string value after STARTS WITH.",
                    _currentExpression,
                    GetCharPosition(tokens, pos),
                    "STARTS WITH syntax: field STARTS WITH 'text'");
            }
            string startsValue = tokens[pos].Value;
            pos++;
            
            return new StringFilter(fieldName, StringFilter.StringOperation.StartsWith, startsValue);
        }
        
        // Handle ENDS WITH operator
        if (pos < tokens.Count && tokens[pos].Type == TokenType.EndsWith)
        {
            pos++; // consume ENDS
            
            if (pos >= tokens.Count || tokens[pos].Type != TokenType.With)
            {
                throw new FilterParseException(
                    "Expected WITH after ENDS keyword.",
                    _currentExpression,
                    GetCharPosition(tokens, pos),
                    "String matching syntax: field ENDS WITH 'text'");
            }
            pos++; // consume WITH
            
            if (pos >= tokens.Count || tokens[pos].Type != TokenType.Value)
            {
                throw new FilterParseException(
                    "Expected string value after ENDS WITH.",
                    _currentExpression,
                    GetCharPosition(tokens, pos),
                    "ENDS WITH syntax: field ENDS WITH 'text'");
            }
            string endsValue = tokens[pos].Value;
            pos++;
            
            return new StringFilter(fieldName, StringFilter.StringOperation.EndsWith, endsValue);
        }
        
        // Handle LIKE operator
        if (pos < tokens.Count && tokens[pos].Type == TokenType.Like)
        {
            pos++; // consume LIKE
            
            if (pos >= tokens.Count || tokens[pos].Type != TokenType.Value)
                throw new ArgumentException("Expected value after LIKE");
            string likePattern = tokens[pos].Value;
            pos++;
            
            return new StringFilter(fieldName, StringFilter.StringOperation.Like, likePattern);
        }
        
        // Handle MATCHES operator (regex)
        if (pos < tokens.Count && tokens[pos].Type == TokenType.Matches)
        {
            pos++; // consume MATCHES
            
            if (pos >= tokens.Count || tokens[pos].Type != TokenType.Value)
                throw new ArgumentException("Expected regex pattern after MATCHES");
            string regexPattern = tokens[pos].Value;
            pos++;
            
            return new RegexFilter(fieldName, regexPattern);
        }
        
        // Handle IS [NOT] NULL operator
        if (pos < tokens.Count && tokens[pos].Type == TokenType.Is)
        {
            pos++; // consume IS
            
            bool isNot = false;
            if (pos < tokens.Count && tokens[pos].Type == TokenType.Not)
            {
                isNot = true;
                pos++; // consume NOT
            }
            
            if (pos >= tokens.Count || tokens[pos].Type != TokenType.Null)
                throw new ArgumentException("Expected NULL after IS [NOT]");
            pos++; // consume NULL
            
            return new NullFilter(fieldName, !isNot);
        }
        
        // Handle BETWEEN specially
        if (pos < tokens.Count && tokens[pos].Type == TokenType.Between)
        {
            pos++; // consume BETWEEN
            
            if (pos >= tokens.Count || tokens[pos].Type != TokenType.Value)
            {
                throw new FilterParseException(
                    "Expected minimum value after BETWEEN.",
                    _currentExpression,
                    GetCharPosition(tokens, pos),
                    "BETWEEN syntax: field BETWEEN min_value AND max_value");
            }
            string minValue = tokens[pos].Value;
            pos++;
            
            if (pos >= tokens.Count || tokens[pos].Type != TokenType.And)
            {
                throw new FilterParseException(
                    "Expected AND keyword in BETWEEN clause.",
                    _currentExpression,
                    GetCharPosition(tokens, pos),
                    "BETWEEN syntax: field BETWEEN min_value AND max_value");
            }
            pos++; // consume AND
            
            if (pos >= tokens.Count || tokens[pos].Type != TokenType.Value)
            {
                throw new FilterParseException(
                    "Expected maximum value after AND in BETWEEN clause.",
                    _currentExpression,
                    GetCharPosition(tokens, pos),
                    "BETWEEN syntax: field BETWEEN min_value AND max_value");
            }
            string maxValue = tokens[pos].Value;
            pos++;
            
            return new RangeFilter(fieldName, minValue, maxValue);
        }
        
        // Parse regular operator
        if (pos >= tokens.Count || tokens[pos].Type != TokenType.Operator)
        {
            string found = pos >= tokens.Count ? "end of expression" : $"'{tokens[pos].Value}'";
            throw new FilterParseException(
                $"Expected comparison operator (=, !=, <, <=, >, >=), but found {found}.",
                _currentExpression,
                GetCharPosition(tokens, pos),
                "Valid operators: =, !=, <, <=, >, >=, IN, BETWEEN, CONTAINS, LIKE, etc.");
        }
        
        string op = tokens[pos].Value;
        pos++;
        
        // Parse value
        if (pos >= tokens.Count || tokens[pos].Type != TokenType.Value)
        {
            throw new FilterParseException(
                $"Expected value after operator '{op}'.",
                _currentExpression,
                GetCharPosition(tokens, pos),
                "Values should be numbers (42) or strings ('text'). Strings must be in quotes.");
        }
        
        string value = tokens[pos].Value;
        pos++;
        
        // Create appropriate filter based on operator
        return op switch
        {
            "=" => new ValueFilter(fieldName, value),
            "!=" => CompositeFilter.Not(new ValueFilter(fieldName, value)),
            ">" => new RangeFilter(fieldName, minValue: value, maxValue: null, includeMin: false),
            ">=" => new RangeFilter(fieldName, minValue: value, maxValue: null, includeMin: true),
            "<" => new RangeFilter(fieldName, minValue: null, maxValue: value, includeMax: false),
            "<=" => new RangeFilter(fieldName, minValue: null, maxValue: value, includeMax: true),
            _ => throw new FilterParseException(
                $"Unknown or unsupported operator: '{op}'.",
                _currentExpression,
                GetCharPosition(tokens, pos - 2), // Back to operator position
                "Valid operators: =, !=, <, <=, >, >=. For other operations use: IN, BETWEEN, CONTAINS, LIKE, MATCHES")
        };
    }
    
    private static List<Token> Tokenize(string expression)
    {
        var tokens = new List<Token>();
        int i = 0;
        
        while (i < expression.Length)
        {
            // Skip whitespace
            if (char.IsWhiteSpace(expression[i]))
            {
                i++;
                continue;
            }
            
            // Parentheses
            if (expression[i] == '(')
            {
                tokens.Add(new Token(TokenType.LeftParen, "("));
                i++;
                continue;
            }
            if (expression[i] == ')')
            {
                tokens.Add(new Token(TokenType.RightParen, ")"));
                i++;
                continue;
            }
            
            // Comma (for IN clause)
            if (expression[i] == ',')
            {
                tokens.Add(new Token(TokenType.Comma, ","));
                i++;
                continue;
            }
            
            // Ternary operator: ? and :
            if (expression[i] == '?')
            {
                tokens.Add(new Token(TokenType.Question, "?"));
                i++;
                continue;
            }
            if (expression[i] == ':')
            {
                tokens.Add(new Token(TokenType.Colon, ":"));
                i++;
                continue;
            }
            
            // C-style logical operators: && and ||
            if (i + 1 < expression.Length)
            {
                string twoChar = expression.Substring(i, 2);
                if (twoChar == "&&")
                {
                    tokens.Add(new Token(TokenType.And, "&&"));
                    i += 2;
                    continue;
                }
                if (twoChar == "||")
                {
                    tokens.Add(new Token(TokenType.Or, "||"));
                    i += 2;
                    continue;
                }
            }
            
            // Single character logical operators: & and |
            if (expression[i] == '&')
            {
                tokens.Add(new Token(TokenType.And, "&"));
                i++;
                continue;
            }
            if (expression[i] == '|')
            {
                tokens.Add(new Token(TokenType.Or, "|"));
                i++;
                continue;
            }
            
            // Comparison operators (check for != before checking for ! as NOT)
            if (expression[i] == '=' || expression[i] == '<' || expression[i] == '>')
            {
                string op = expression[i].ToString();
                i++;
                if (i < expression.Length && expression[i] == '=')
                {
                    op += '=';
                    i++;
                }
                tokens.Add(new Token(TokenType.Operator, op));
                continue;
            }
            
            // Handle ! - could be != or NOT
            if (expression[i] == '!')
            {
                i++;
                if (i < expression.Length && expression[i] == '=')
                {
                    // It's the != operator
                    tokens.Add(new Token(TokenType.Operator, "!="));
                    i++;
                }
                else
                {
                    // It's the NOT operator
                    tokens.Add(new Token(TokenType.Not, "!"));
                }
                continue;
            }
            
            // String literals
            if (expression[i] == '\'' || expression[i] == '"')
            {
                char quote = expression[i];
                i++; // skip opening quote
                var sb = new StringBuilder();
                while (i < expression.Length && expression[i] != quote)
                {
                    sb.Append(expression[i]);
                    i++;
                }
                if (i >= expression.Length)
                {
                    throw new FilterParseException(
                        "Unterminated string literal - missing closing quote.",
                        expression,
                        i - 1,
                        $"String literals must be enclosed in matching quotes: 'text' or \"text\"");
                }
                i++; // skip closing quote
                tokens.Add(new Token(TokenType.Value, sb.ToString()));
                continue;
            }
            
            // Keywords and identifiers
            if (char.IsLetter(expression[i]) || expression[i] == '_')
            {
                var sb = new StringBuilder();
                while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_'))
                {
                    sb.Append(expression[i]);
                    i++;
                }
                
                string word = sb.ToString();
                string upper = word.ToUpperInvariant();
                
                TokenType type = upper switch
                {
                    "AND" => TokenType.And,
                    "OR" => TokenType.Or,
                    "NOT" => TokenType.Not,
                    "BETWEEN" => TokenType.Between,
                    "IN" => TokenType.In,
                    "CONTAINS" => TokenType.Contains,
                    "STARTS" => TokenType.StartsWith,
                    "ENDS" => TokenType.EndsWith,
                    "LIKE" => TokenType.Like,
                    "MATCHES" => TokenType.Matches,
                    "IS" => TokenType.Is,
                    "NULL" => TokenType.Null,
                    "WITH" => TokenType.With,
                    _ => TokenType.Identifier
                };
                
                tokens.Add(new Token(type, word));
                continue;
            }
            
            // Numbers (as values)
            if (char.IsDigit(expression[i]))
            {
                var sb = new StringBuilder();
                while (i < expression.Length && (char.IsDigit(expression[i]) || expression[i] == '.'))
                {
                    sb.Append(expression[i]);
                    i++;
                }
                tokens.Add(new Token(TokenType.Value, sb.ToString()));
                continue;
            }
            
            throw new FilterParseException(
                $"Unexpected character: '{expression[i]}'",
                expression,
                i,
                "Only letters, numbers, quotes, operators (= < > !), parentheses, and special characters (? : , & |) are allowed.");
        }
        
        return tokens;
    }
    
    private enum TokenType
    {
        Identifier,
        Operator,
        Value,
        And,
        Or,
        Not,
        Between,
        In,
        Contains,
        StartsWith,
        EndsWith,
        Like,
        Matches,
        Is,
        Null,
        With,
        Comma,
        LeftParen,
        RightParen,
        Question,    // ?
        Colon        // :
    }
    
    private class Token
    {
        public TokenType Type { get; }
        public string Value { get; }
        
        public Token(TokenType type, string value)
        {
            Type = type;
            Value = value;
        }
        
        public override string ToString() => $"{Type}: {Value}";
    }
}

