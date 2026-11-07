#region

using System.Text;
using CS2DemoKit.Analysis.Catalog;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>The two connectives of the condition language, whichever way the author spelled them.</summary>
public enum ConditionConnective
{
    /// <summary><c>&amp;&amp;</c> or <c>and</c>.</summary>
    And,

    /// <summary><c>||</c> or <c>or</c>.</summary>
    Or
}

/// <summary>One node of a parsed condition. <see cref="Text" /> is the node's exact source span.</summary>
/// <param name="Text">The node's own text, byte for byte as the author wrote it, outer whitespace excluded.</param>
public abstract record ConditionNode(string Text);

/// <summary>
///     A comparison (<c>event.Penetrated &gt;= 1</c>, <c>event.Weapon in [awp, ssg08]</c>) or a bare
///     path (<c>player.traded</c>). An operand is any run of tokens up to the comparator, so a
///     <c>compute:</c> formula or <c>params.max * players</c> is one operand, kept verbatim.
/// </summary>
/// <param name="Text">The leaf's exact source span.</param>
/// <param name="Path">The left operand as written.</param>
/// <param name="Comparator">The comparator as written, or <c>null</c> for a bare path.</param>
/// <param name="Literal">The right operand as written, or <c>null</c> for a bare path.</param>
public sealed record ConditionLeaf(string Text, string Path, string? Comparator, string? Literal) : ConditionNode(Text)
{
    /// <summary>The whitespace between the path and the comparator, kept so an edit does not respace the row.</summary>
    internal string LeftGap { get; init; } = " ";

    /// <summary>The whitespace between the comparator and the literal.</summary>
    internal string RightGap { get; init; } = " ";

    /// <summary>Renders a leaf from its parts, reusing this leaf's spacing.</summary>
    internal string Render(string path, string? comparator, string? literal) =>
        comparator is null ? path : path + LeftGap + comparator + RightGap + literal;
}

/// <summary>A negation: <c>not x</c> or <c>!x</c>.</summary>
/// <param name="Text">The exact source span.</param>
/// <param name="Spelling"><c>not</c> or <c>!</c>.</param>
/// <param name="Operand">What is negated.</param>
public sealed record ConditionNegation(string Text, string Spelling, ConditionNode Operand) : ConditionNode(Text);

/// <summary>A parenthesised sub-expression.</summary>
/// <param name="Text">The exact source span, parentheses included.</param>
/// <param name="Inner">The expression inside.</param>
public sealed record ConditionGroup(string Text, ConditionNode Inner) : ConditionNode(Text);

/// <summary>
///     Two or more operands joined by one connective kind. <c>and</c> binds tighter than <c>or</c>, so
///     <c>a &amp;&amp; b || c</c> is an Or junction whose first operand is an And junction.
/// </summary>
/// <param name="Text">The exact source span.</param>
/// <param name="Connective">The kind.</param>
/// <param name="Operands">The operands in source order.</param>
/// <param name="Separators">
///     The exact text between consecutive operands, whitespace and connective spelling included
///     (<c>" || "</c>, <c>" or "</c>); one fewer than the operands.
/// </param>
public sealed record ConditionJunction(
    string Text,
    ConditionConnective Connective,
    IReadOnlyList<ConditionNode> Operands,
    IReadOnlyList<string> Separators) : ConditionNode(Text);

/// <summary>One row of a flat expression: what a face editor shows and edits.</summary>
/// <param name="Path">The left operand as written.</param>
/// <param name="Comparator">The comparator as written, or <c>null</c> for a bare path.</param>
/// <param name="Literal">The right operand as written, or <c>null</c> for a bare path.</param>
/// <param name="Text">The row's exact source text.</param>
public sealed record ConditionRow(string Path, string? Comparator, string? Literal, string Text);

/// <summary>
///     A <c>where:</c>, <c>when:</c>, <c>while:</c> or <c>compute:</c> text as a tree of comparisons and
///     bare paths joined by <c>and</c>/<c>or</c>, and as rows when the tree is flat.
///     <para>
///         The parser is tolerant and keeps every span verbatim: the tree is a view over the author's
///         text, not a normalisation of it. A text that does not parse comes back whole with
///         <see cref="Error" /> set and no rows, so a face can still show it and the splice writer still
///         keeps it. The writers below change only the row named and keep every other byte, the same
///         promise <see cref="RulesetEdits" /> makes for the document around the expression.
///     </para>
///     <para>
///         Flat means the root is one leaf, or one junction whose operands are all leaves. A group, a
///         negation or a mixed-precedence tree is shown as text only. The corpus survey in the tests
///         measured 331 of 336 expressions flat (180 bare paths, 84 single comparisons, 38 lists of two
///         to ten rows) and 5 nested, every one a group inside a list.
///     </para>
/// </summary>
public sealed class ConditionExpression
{
    private static readonly IReadOnlyList<ConditionRow> NoRows = [];

    private readonly IReadOnlyList<ConditionLeaf> _leaves;
    private readonly string _prefix;
    private readonly IReadOnlyList<string> _separators;
    private readonly string _suffix;

    private ConditionExpression(string text, ConditionNode? root, string? error, string prefix, string suffix,
        IReadOnlyList<ConditionLeaf> leaves, IReadOnlyList<string> separators, ConditionConnective? connective)
    {
        Text = text;
        Root = root;
        Error = error;
        _prefix = prefix;
        _suffix = suffix;
        _leaves = leaves;
        _separators = separators;
        Connective = connective;
        IsFlat = error is null && (root is null || root is ConditionLeaf ||
                                   root is ConditionJunction j && j.Operands.All(o => o is ConditionLeaf));
        Rows = IsFlat ? [.. leaves.Select(l => new ConditionRow(l.Path, l.Comparator, l.Literal, l.Text))] : NoRows;
    }

    /// <summary>The text as given.</summary>
    public string Text { get; }

    /// <summary>The tree, or <c>null</c> for an empty text or a text that did not parse.</summary>
    public ConditionNode? Root { get; }

    /// <summary>Why the text did not parse, or <c>null</c>.</summary>
    public string? Error { get; }

    /// <summary>Whether <see cref="Rows" /> describe the whole expression.</summary>
    public bool IsFlat { get; }

    /// <summary>The rows of a flat expression; empty when not flat or when the text is empty.</summary>
    public IReadOnlyList<ConditionRow> Rows { get; }

    /// <summary>The connective joining the rows, or <c>null</c> for one row or none.</summary>
    public ConditionConnective? Connective { get; }

    /// <summary>The connective as the author spelled it (<c>&amp;&amp;</c>, <c>and</c>, <c>||</c>, <c>or</c>), or <c>null</c>.</summary>
    public string? ConnectiveSpelling =>
        Connective is null || _separators.Count == 0 ? null : _separators[^1].Trim();

    /// <summary>Parses a condition text. Never throws: a text that does not parse sets <see cref="Error" />.</summary>
    /// <param name="text">The expression text, <c>null</c> for none.</param>
    /// <returns>The expression.</returns>
    public static ConditionExpression Parse(string? text)
    {
        text ??= "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ConditionExpression(text, null, null, text, "", [], [], null);
        }

        Parser parser;
        ConditionNode root;
        try
        {
            parser = new Parser(text);
            root = parser.ParseAll();
        }
        catch (FormatException e)
        {
            return new ConditionExpression(text, null, e.Message, "", "", [], [], null);
        }

        (int start, int end) = parser.SpanOf(root);
        string prefix = text[..start];
        string suffix = text[end..];
        switch (root)
        {
            case ConditionLeaf leaf:
                return new ConditionExpression(text, root, null, prefix, suffix, [leaf], [], null);
            case ConditionJunction junction when junction.Operands.All(o => o is ConditionLeaf):
                return new ConditionExpression(text, root, null, prefix, suffix,
                    [.. junction.Operands.Cast<ConditionLeaf>()], junction.Separators, junction.Connective);
            default:
                return new ConditionExpression(text, root, null, prefix, suffix, [], [], null);
        }
    }

    /// <summary>
    ///     Writes the expression back from its parts. Unchanged, this is <see cref="Text" /> byte for byte,
    ///     which the corpus round-trip test holds the parser to.
    /// </summary>
    /// <returns>The text.</returns>
    public string ToText()
    {
        if (!IsFlat)
        {
            return Text;
        }

        return Join([.. _leaves.Select(l => l.Text)], _separators);
    }

    /// <summary>Replaces one row and keeps every other byte, the row's own spacing included.</summary>
    /// <param name="index">The row.</param>
    /// <param name="path">The new left operand.</param>
    /// <param name="comparator">The new comparator, or <c>null</c> for a bare path.</param>
    /// <param name="literal">The new right operand, <c>null</c> with a <c>null</c> comparator.</param>
    /// <returns>The new text.</returns>
    /// <exception cref="InvalidOperationException">The expression is not flat.</exception>
    public string WithRow(int index, string path, string? comparator, string? literal)
    {
        RequireFlat();
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _leaves.Count);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        List<string> texts = [.. _leaves.Select(l => l.Text)];
        texts[index] = _leaves[index].Render(path.Trim(), comparator?.Trim(), literal?.Trim());
        return Join(texts, _separators);
    }

    /// <summary>
    ///     Appends a row. The separator copies the author's last one when there is one; a single row
    ///     or an empty text gets <c>&amp;&amp;</c> or <c>||</c>, the corpus's majority spelling, unless
    ///     <paramref name="spelling" /> says otherwise.
    /// </summary>
    /// <param name="path">The left operand.</param>
    /// <param name="comparator">The comparator, or <c>null</c> for a bare path.</param>
    /// <param name="literal">The right operand, <c>null</c> with a <c>null</c> comparator.</param>
    /// <param name="connective">The kind, which must match the author's when there already are two rows.</param>
    /// <param name="spelling">The connective text for a first join, <c>and</c> or <c>&amp;&amp;</c>, without spaces.</param>
    /// <returns>The new text.</returns>
    /// <exception cref="InvalidOperationException">The expression is not flat, or the kind differs from the author's.</exception>
    public string WithRowAdded(string path, string? comparator, string? literal,
        ConditionConnective connective = ConditionConnective.And, string? spelling = null)
    {
        RequireFlat();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string leaf = new ConditionLeaf("", "", null, null).Render(path.Trim(), comparator?.Trim(), literal?.Trim());
        if (_leaves.Count == 0)
        {
            return leaf;
        }

        if (Connective is { } existing && existing != connective)
        {
            throw new InvalidOperationException(
                $"the rows are joined by {ConnectiveSpelling}; mixing in {connective} needs parentheses");
        }

        string separator = _separators.Count > 0
            ? _separators[^1]
            : " " + (spelling?.Trim() ?? (connective == ConditionConnective.And ? "&&" : "||")) + " ";
        List<string> texts = [.. _leaves.Select(l => l.Text), leaf];
        return Join(texts, [.. _separators, separator]);
    }

    /// <summary>Removes one row and the separator beside it. Removing the only row gives an empty text.</summary>
    /// <param name="index">The row.</param>
    /// <returns>The new text.</returns>
    /// <exception cref="InvalidOperationException">The expression is not flat.</exception>
    public string WithRowRemoved(int index)
    {
        RequireFlat();
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _leaves.Count);
        if (_leaves.Count == 1)
        {
            return "";
        }

        List<string> texts = [.. _leaves.Select(l => l.Text)];
        List<string> separators = [.. _separators];
        texts.RemoveAt(index);
        separators.RemoveAt(index == 0 ? 0 : index - 1);
        return Join(texts, separators);
    }

    /// <summary>
    ///     Joins every row by <paramref name="connective" /> instead, keeping each separator's spacing and
    ///     spelling family: <c>&amp;&amp;</c> becomes <c>||</c> and <c>and</c> becomes <c>or</c>. One row
    ///     or none has nothing to switch, so the text comes back unchanged.
    /// </summary>
    /// <param name="connective">The kind.</param>
    /// <returns>The new text.</returns>
    /// <exception cref="InvalidOperationException">The expression is not flat.</exception>
    public string WithConnective(ConditionConnective connective)
    {
        RequireFlat();
        if (Connective is null || Connective == connective)
        {
            return ToText();
        }

        List<string> separators = [];
        foreach (string separator in _separators)
        {
            string spelling = separator.Trim();
            string replacement = spelling switch
            {
                "&&" => "||",
                "||" => "&&",
                "and" => "or",
                _ => "and"
            };
            int at = separator.IndexOf(spelling, StringComparison.Ordinal);
            separators.Add(separator[..at] + replacement + separator[(at + spelling.Length)..]);
        }

        return Join([.. _leaves.Select(l => l.Text)], separators);
    }

    private void RequireFlat()
    {
        if (!IsFlat)
        {
            throw new InvalidOperationException(Error is null
                ? "the expression is not a flat list of rows; edit it as text"
                : "the expression did not parse: " + Error);
        }
    }

    private string Join(List<string> texts, IReadOnlyList<string> separators)
    {
        StringBuilder sb = new(_prefix);
        for (int i = 0; i < texts.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(separators[i - 1]);
            }

            sb.Append(texts[i]);
        }

        return sb.Append(_suffix).ToString();
    }

    // ── the parser ───────────────────────────────────────────────────────────────────────────────

    private enum TokenKind
    {
        Word,
        String,
        Symbol,
        Open,
        Close,
        OpenList,
        CloseList,
        Comma,
        End
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Start, int End);

    /// <summary>
    ///     Recursive descent over the tokens, <c>or</c> below <c>and</c> below <c>not</c> below a group or a
    ///     comparison. An operand is scanned, not parsed: everything up to a comparator or connective at
    ///     bracket depth zero, so arithmetic, calls and lists ride along verbatim.
    /// </summary>
    private sealed class Parser
    {
        private readonly string _text;
        private readonly List<Token> _tokens;
        private readonly Dictionary<ConditionNode, (int Start, int End)> _spans = new(ReferenceEqualityComparer.Instance);
        private int _pos;

        public Parser(string text)
        {
            _text = text;
            _tokens = Lex(text);
        }

        private Token Peek => _tokens[_pos];

        public ConditionNode ParseAll()
        {
            ConditionNode node = ParseOr();
            if (Peek.Kind != TokenKind.End)
            {
                throw new FormatException($"unexpected '{Peek.Text}' at {Peek.Start}");
            }

            return node;
        }

        private ConditionNode ParseOr() => ParseJunction(ConditionConnective.Or, ParseAnd);

        private ConditionNode ParseAnd() => ParseJunction(ConditionConnective.And, ParseUnary);

        private ConditionNode ParseJunction(ConditionConnective kind, Func<ConditionNode> next)
        {
            ConditionNode first = next();
            if (!IsConnective(Peek, kind))
            {
                return first;
            }

            List<ConditionNode> operands = [first];
            List<string> separators = [];
            List<(int Start, int End)> spans = [SpanOf(first)];
            while (IsConnective(Peek, kind))
            {
                int separatorStart = spans[^1].End;
                _pos++;
                ConditionNode operand = next();
                (int Start, int End) span = SpanOf(operand);
                separators.Add(_text[separatorStart..span.Start]);
                operands.Add(operand);
                spans.Add(span);
            }

            return Record(new ConditionJunction(_text[spans[0].Start..spans[^1].End], kind, operands, separators),
                spans[0].Start, spans[^1].End);
        }

        private ConditionNode ParseUnary()
        {
            Token token = Peek;
            if ((token.Kind == TokenKind.Symbol && token.Text == "!") || (token.Kind == TokenKind.Word && token.Text == "not"))
            {
                _pos++;
                ConditionNode operand = ParseUnary();
                (int Start, int End) span = SpanOf(operand);
                return Record(new ConditionNegation(_text[token.Start..span.End], token.Text, operand), token.Start, span.End);
            }

            return ParsePrimary();
        }

        private ConditionNode ParsePrimary()
        {
            Token token = Peek;
            if (token.Kind == TokenKind.End)
            {
                throw new FormatException($"expected an operand at {token.Start}");
            }

            if (token.Kind == TokenKind.Open && IsGroup())
            {
                _pos++;
                ConditionNode inner = ParseOr();
                if (Peek.Kind != TokenKind.Close)
                {
                    throw new FormatException($"expected ')' at {Peek.Start}");
                }

                Token close = Peek;
                _pos++;
                return Record(new ConditionGroup(_text[token.Start..close.End], inner), token.Start, close.End);
            }

            (int Start, int End) left = ScanOperand();
            Token comparator = Peek;
            if (!IsComparator(comparator))
            {
                string bare = _text[left.Start..left.End];
                return Record(new ConditionLeaf(bare, bare, null, null), left.Start, left.End);
            }

            _pos++;
            (int Start, int End) right = ScanOperand();
            return Record(new ConditionLeaf(_text[left.Start..right.End], _text[left.Start..left.End], comparator.Text,
                _text[right.Start..right.End])
            {
                LeftGap = _text[left.End..comparator.Start],
                RightGap = _text[comparator.End..right.Start]
            }, left.Start, right.End);
        }

        /// <summary>
        ///     Whether the '(' at the cursor opens a boolean group rather than an arithmetic one: a group
        ///     is followed by a connective, a ')' or the end, and <c>(a + b) &gt; 3</c> is not.
        /// </summary>
        private bool IsGroup()
        {
            int depth = 0;
            for (int i = _pos; i < _tokens.Count; i++)
            {
                switch (_tokens[i].Kind)
                {
                    case TokenKind.Open or TokenKind.OpenList:
                        depth++;
                        break;
                    case TokenKind.Close or TokenKind.CloseList:
                        depth--;
                        if (depth == 0)
                        {
                            Token after = _tokens[i + 1];
                            return after.Kind is TokenKind.End or TokenKind.Close
                                   || IsConnective(after, ConditionConnective.And)
                                   || IsConnective(after, ConditionConnective.Or);
                        }

                        break;
                    case TokenKind.End:
                        return false;
                }
            }

            return false;
        }

        private (int Start, int End) ScanOperand()
        {
            int first = _pos;
            int depth = 0;
            TokenKind previous = TokenKind.End;
            while (true)
            {
                Token token = Peek;
                if (token.Kind == TokenKind.End)
                {
                    break;
                }

                if (depth == 0 && (token.Kind == TokenKind.Close || IsComparator(token)
                                                                 || IsConnective(token, ConditionConnective.And)
                                                                 || IsConnective(token, ConditionConnective.Or)))
                {
                    break;
                }

                // Two values side by side ('1 b') is no operand in any grammar the engine reads.
                if (token.Kind is TokenKind.Word or TokenKind.String
                    && previous is TokenKind.Word or TokenKind.String or TokenKind.Close or TokenKind.CloseList)
                {
                    throw new FormatException($"unexpected '{token.Text}' at {token.Start}");
                }

                switch (token.Kind)
                {
                    case TokenKind.Open or TokenKind.OpenList:
                        depth++;
                        break;
                    case TokenKind.Close or TokenKind.CloseList:
                        depth--;
                        if (depth < 0)
                        {
                            throw new FormatException($"unexpected '{token.Text}' at {token.Start}");
                        }

                        break;
                }

                previous = token.Kind;
                _pos++;
            }

            if (_pos == first)
            {
                throw new FormatException($"expected an operand at {Peek.Start}");
            }

            if (depth != 0)
            {
                throw new FormatException($"unclosed bracket in '{_text[_tokens[first].Start.._tokens[_pos - 1].End]}'");
            }

            return (_tokens[first].Start, _tokens[_pos - 1].End);
        }

        public (int Start, int End) SpanOf(ConditionNode node) => _spans[node];

        private T Record<T>(T node, int start, int end) where T : ConditionNode
        {
            _spans[node] = (start, end);
            return node;
        }

        private static bool IsConnective(Token token, ConditionConnective kind) =>
            kind == ConditionConnective.And
                ? (token.Kind == TokenKind.Symbol && token.Text == "&&") || (token.Kind == TokenKind.Word && token.Text == "and")
                : (token.Kind == TokenKind.Symbol && token.Text == "||") || (token.Kind == TokenKind.Word && token.Text == "or");

        private static bool IsComparator(Token token) =>
            (token.Kind == TokenKind.Symbol && token.Text is "==" or "!=" or ">=" or "<=" or ">" or "<")
            || (token.Kind == TokenKind.Word && token.Text == "in");

        private static List<Token> Lex(string text)
        {
            List<Token> tokens = [];
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                int start = i;
                if (char.IsLetterOrDigit(c) || c is '_' or '.' or '$')
                {
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.' or '$'))
                    {
                        i++;
                    }

                    tokens.Add(new Token(TokenKind.Word, text[start..i], start, i));
                    continue;
                }

                if (c is '"' or '\'')
                {
                    i++;
                    while (i < text.Length && text[i] != c)
                    {
                        i += text[i] == '\\' ? 2 : 1;
                    }

                    if (i >= text.Length)
                    {
                        throw new FormatException($"unterminated string at {start}");
                    }

                    i++;
                    tokens.Add(new Token(TokenKind.String, text[start..i], start, i));
                    continue;
                }

                TokenKind kind = c switch
                {
                    '(' => TokenKind.Open,
                    ')' => TokenKind.Close,
                    '[' => TokenKind.OpenList,
                    ']' => TokenKind.CloseList,
                    ',' => TokenKind.Comma,
                    _ => TokenKind.Symbol
                };
                if (kind != TokenKind.Symbol)
                {
                    i++;
                    tokens.Add(new Token(kind, text[start..i], start, i));
                    continue;
                }

                string two = i + 1 < text.Length ? text.Substring(i, 2) : "";
                if (two is "==" or "!=" or ">=" or "<=" or "&&" or "||")
                {
                    i += 2;
                }
                else if (c is '>' or '<' or '!' or '+' or '-' or '*' or '/' or '%')
                {
                    i++;
                }
                else
                {
                    throw new FormatException($"unexpected '{c}' at {start}");
                }

                tokens.Add(new Token(TokenKind.Symbol, text[start..i], start, i));
            }

            tokens.Add(new Token(TokenKind.End, "", text.Length, text.Length));
            return tokens;
        }
    }
}

/// <summary>
///     The paths a row of a condition may name, read from the catalog for one view.
///     <para>
///         The catalog carries the view's event fields, the view's facets by their enrichment node,
///         and the built-in context rules by their v2 path. It does not carry <c>params.*</c>, a
///         <c>define:</c> name, a qualified read of another ruleset (<c>kast.kast_pct</c>), the
///         <c>player.*</c> handle fields a <c>where:</c> compares with (<c>player.slot</c>,
///         <c>player.team</c>, <c>player.place</c>, <c>player.armor</c>, <c>player.equipment_value</c>,
///         <c>player.active_weapon_clip</c>), or an <c>enrich.*</c> node no facet of the view reads
///         (<c>enrich.clutch.*</c>, <c>enrich.kill.flash_attacker_slot</c>, <c>enrich.kill.spray_*</c>).
///         The corpus test measures that list: 17 distinct paths of the 389 the corpus names, all in
///         those three families.
///     </para>
/// </summary>
public static class ConditionVocabulary
{
    /// <summary>Lists the paths for one view: event fields, facet enrichments, contexts, then the siblings given.</summary>
    /// <param name="catalog">The embedded catalog.</param>
    /// <param name="view">The view the node fires on, or <c>null</c> for none.</param>
    /// <param name="siblingStatIds">Stat ids of the same document a row may read.</param>
    /// <returns>Distinct paths in that order.</returns>
    public static IReadOnlyList<string> PathsFor(CatalogRoot catalog, string? view, IEnumerable<string>? siblingStatIds = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        List<string> paths = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        void Add(string? path)
        {
            if (!string.IsNullOrEmpty(path) && seen.Add(path))
            {
                paths.Add(path);
            }
        }

        CatalogView? scope = view is null
            ? null
            : catalog.Views.FirstOrDefault(v => string.Equals(v.Name, view, StringComparison.Ordinal));
        if (scope is not null)
        {
            foreach (CatalogEvent evt in EventsOf(catalog, scope))
            {
                foreach (CatalogField field in evt.Fields)
                {
                    Add(field.V2Name ?? "event." + field.Name);
                }
            }

            foreach (CatalogFacet facet in scope.Facets)
            {
                Add(facet.Enrichment);
            }
        }

        foreach (CatalogContextRule context in catalog.Contexts)
        {
            Add(context.V2Name);
        }

        foreach (string sibling in siblingStatIds ?? [])
        {
            Add(sibling);
        }

        return paths;
    }

    /// <summary>
    ///     The registered events a view fires on: its logical event when that is registered under the
    ///     same name, else the concrete wire events its profiles resolve it to (<c>round_end</c> is
    ///     <c>round_officially_ended</c> and two others).
    /// </summary>
    private static IEnumerable<CatalogEvent> EventsOf(CatalogRoot catalog, CatalogView view)
    {
        CatalogEvent? logical = catalog.Events.FirstOrDefault(e => string.Equals(e.Name, view.Event, StringComparison.Ordinal));
        if (logical is not null)
        {
            return [logical];
        }

        HashSet<string> concrete = new(view.Profiles.SelectMany(p => p.ConcreteEvents), StringComparer.Ordinal);
        return catalog.Events.Where(e => concrete.Contains(e.Name));
    }
}
