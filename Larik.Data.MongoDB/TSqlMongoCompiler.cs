using System.Globalization;
using System.Text;
using MongoDB.Bson;

namespace Larik.Data.MongoDB;

/// <summary>
/// Compiles a T-SQL statement (<c>SELECT</c> / <c>INSERT</c> / <c>UPDATE</c> / <c>DELETE</c> /
/// <c>USE</c>) into a driver command. Synchronous and self-contained — no I/O, no reflection — so the
/// whole language can be golden-tested without a server. Unsupported constructs are rejected with a
/// message that names the offending part rather than being silently mis-translated.
/// </summary>
internal static class TSqlMongoCompiler
{
    private const string ReservedWords =
        "SELECT FROM WHERE GROUP BY HAVING ORDER LIMIT OFFSET FETCH JOIN INNER LEFT RIGHT FULL OUTER CROSS ON AND OR NOT AS UNION EXCEPT INTERSECT ASC DESC IS IN LIKE ILIKE BETWEEN CASE WHEN THEN ELSE END DISTINCT TOP INTO ROWS ROW ONLY NULL TRUE FALSE INSERT UPDATE DELETE VALUES SET USE DATE TIMESTAMP DATETIME";

    private static readonly HashSet<string> Reserved = new(ReservedWords.Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Aggregates = new(["COUNT", "SUM", "AVG", "MIN", "MAX"], StringComparer.OrdinalIgnoreCase);

    public static MongoCommand Parse(string src, IReadOnlyDictionary<string, string>? fieldTypes = null)
    {
        var tokens = Lex(src);
        if (tokens.Count == 0)
        {
            throw new TranslationException("Nothing to translate — the statement is empty.");
        }

        var p = new Reader(tokens);
        if (p.Eat("SELECT"))
        {
            return new SelectCompiler(fieldTypes).Compile(p.ReadSelect(), src);
        }

        if (p.Eat("UPDATE"))
        {
            return ReadUpdate(p, src, fieldTypes);
        }

        if (p.Eat("DELETE"))
        {
            return ReadDelete(p, src, fieldTypes);
        }

        if (p.Eat("INSERT"))
        {
            return ReadInsert(p, src);
        }

        if (p.Eat("USE"))
        {
            var db = p.Ident("a database name");
            p.Finish();
            return new MUse(db, src);
        }

        throw new TranslationException(
            "MongoDB supports SELECT, INSERT, UPDATE, DELETE, USE and mongosh (db.collection.…) statements.");
    }

    /// <summary>
    /// Structural equality over the AST. Records alone are not enough: <see cref="ColX"/>/<see cref="CallX"/>
    /// carry <see cref="List{T}"/> members that compare by reference, so two spelled-identically fields in
    /// GROUP BY and SELECT would otherwise never match.
    /// </summary>
    private static bool XEq(X a, X b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        return (a, b) switch
        {
            (ColX ca, ColX cb) => ListEq(ca.Parts, cb.Parts),
            (Lit la, Lit lb) => la.Kind == lb.Kind && la.Text == lb.Text,
            (Neg na, Neg nb) => XEq(na.Inner, nb.Inner),
            (Not na, Not nb) => XEq(na.Inner, nb.Inner),
            (Bin ba, Bin bb) => ba.Op == bb.Op && XEq(ba.L, bb.L) && XEq(ba.R, bb.R),
            (IsNullX ia, IsNullX ib) => ia.Negated == ib.Negated && XEq(ia.Inner, ib.Inner),
            (InX ia, InX ib) => ia.Negated == ib.Negated && XEq(ia.Inner, ib.Inner) && ListEq(ia.List, ib.List),
            (BetweenX ba, BetweenX bb) => ba.Negated == bb.Negated && XEq(ba.Inner, bb.Inner) && XEq(ba.Low, bb.Low) && XEq(ba.High, bb.High),
            (LikeX la, LikeX lb) => la.Negated == lb.Negated && XEq(la.Inner, lb.Inner) && XEq(la.Pattern, lb.Pattern),
            (CallX fa, CallX fb) => fa.Name == fb.Name && fa.Star == fb.Star && fa.Distinct == fb.Distinct && ListEq(fa.Args, fb.Args),
            _ => false,
        };
    }

    private static bool ListEq<T>(List<T> a, List<T> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var index = 0; index < a.Count; index++)
        {
            var (x, y) = (a[index], b[index]);
            var eq = x is X xe && y is X ye ? XEq(xe, ye) : Equals(x, y);
            if (!eq)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A flat, cheap document-key for an output column or join field.</summary>
    private static string SanitizeName(string name) =>
        string.IsNullOrWhiteSpace(name) ? "expr" : name.Replace('.', '_').TrimStart('$');

    // ============================================================================
    // Lexer
    // ============================================================================

    internal enum TokKind { Ident, Int, Float, Str, Op }

    internal sealed record Token(TokKind Kind, string Text, bool Quoted, int Offset);

    private static readonly string[] Operators = ["<>", "!=", "<=", ">=", "=", "<", ">", "+", "-", "*", "/", "%", "(", ")", ",", ".", ";"];

    private static List<Token> Lex(string src)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < src.Length)
        {
            var c = src[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (src.AsSpan(i).StartsWith("--") || src.AsSpan(i).StartsWith("//"))
            {
                var nl = src.IndexOfAny(['\n', '\r'], i);
                i = nl < 0 ? src.Length : nl;
                continue;
            }

            if (src.AsSpan(i).StartsWith("/*"))
            {
                var close = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? src.Length : close + 2;
                continue;
            }

            var offset = i;
            if ((c == 'N' || c == 'n') && i + 1 < src.Length && src[i + 1] == '\'')
            {
                i++; // N'...' is a '...' literal with a hint marker.
            }

            if (src[i] == '\'')
            {
                var value = new StringBuilder();
                i++;
                while (i < src.Length)
                {
                    var ch = src[i];
                    if (ch == '\'')
                    {
                        if (i + 1 < src.Length && src[i + 1] == '\'')
                        {
                            value.Append('\'');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    value.Append(ch);
                    i++;
                }

                tokens.Add(new Token(TokKind.Str, value.ToString(), false, offset));
                continue;
            }

            if (c == '"' || c == '`' || c == '[')
            {
                var close = c == '[' ? ']' : c;
                var bodyStart = i + 1;
                var end = src.IndexOf(close, bodyStart);
                if (end < 0)
                {
                    throw new TranslationException($"Unterminated quoted identifier at offset {offset}.");
                }

                tokens.Add(new Token(TokKind.Ident, src[bodyStart..end], true, offset));
                i = end + 1;
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < src.Length && char.IsAsciiDigit(src[i + 1])))
            {
                var start = i;
                var isFloat = c == '.';
                i++;
                while (i < src.Length)
                {
                    var d = src[i];
                    if (char.IsAsciiDigit(d))
                    {
                        i++;
                    }
                    else if (d == '.' && !isFloat)
                    {
                        isFloat = true;
                        i++;
                    }
                    else if ((d == 'e' || d == 'E') && i > start)
                    {
                        isFloat = true;
                        i++;
                        if (i < src.Length && (src[i] == '+' || src[i] == '-'))
                        {
                            i++;
                        }
                    }
                    else
                    {
                        break;
                    }
                }

                tokens.Add(new Token(isFloat ? TokKind.Float : TokKind.Int, src[start..i], false, offset));
                continue;
            }

            if (char.IsLetter(c) || c == '_' || c == '$' || c == '@')
            {
                var start = i;
                i++;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_' || src[i] == '$' || src[i] == '@'))
                {
                    i++;
                }

                tokens.Add(new Token(TokKind.Ident, src[start..i], false, offset));
                continue;
            }

            var matched = false;
            foreach (var op in Operators)
            {
                if (src.AsSpan(i).StartsWith(op))
                {
                    tokens.Add(new Token(TokKind.Op, op, false, offset));
                    i += op.Length;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                throw new TranslationException($"Unexpected character '{c}' in the statement.");
            }
        }

        return tokens;
    }

    // ============================================================================
    // AST
    // ============================================================================

    internal enum BinOp { And, Or, Eq, Ne, Lt, Le, Gt, Ge, Add, Sub, Mul, Div, Mod }

    internal abstract record X;
    internal enum LitKind { Null, Bool, Int, Float, Str, Date }
    internal sealed record Lit(string Text, LitKind Kind) : X;
    internal sealed record Neg(X Inner) : X;
    internal sealed record Not(X Inner) : X;
    internal sealed record Bin(BinOp Op, X L, X R) : X;
    internal sealed record IsNullX(X Inner, bool Negated) : X;
    internal sealed record InX(X Inner, List<X> List, bool Negated) : X;
    internal sealed record BetweenX(X Inner, X Low, X High, bool Negated) : X;
    internal sealed record LikeX(X Inner, X Pattern, bool Negated) : X;
    internal sealed record ColX(List<string> Parts) : X;
    internal sealed record CallX(string Name, List<X> Args, bool Distinct, bool Star) : X;

    internal sealed record TableRef(string Name, string? Alias);
    internal sealed record JoinRef(bool Left, TableRef Table, X On);
    internal sealed record SelectItem(bool Wildcard, X? Value, string? Alias);
    internal sealed record Query(
        bool Distinct,
        List<SelectItem> Items,
        TableRef From,
        List<JoinRef> Joins,
        X? Filter,
        List<X> GroupBy,
        X? Having,
        List<(X Expr, bool Desc)> OrderBy,
        long? Limit,
        long? Offset);

    // ============================================================================
    // Parser
    // ============================================================================

    private sealed class Reader(List<Token> tokens)
    {
        private int _pos;

        public Token? Peek => _pos < tokens.Count ? tokens[_pos] : null;

        public string Found()
        {
            var t = Peek;
            return t is null ? " but reached the end of the statement" : $" but found '{t.Text}'";
        }

        public bool IsKeyword(string kw) =>
            Peek is { Kind: TokKind.Ident, Quoted: false } t && t.Text.Equals(kw, StringComparison.OrdinalIgnoreCase);

        public bool AtOp(string op) => Peek is { Kind: TokKind.Op } t && t.Text == op;

        public bool Eat(string kw)
        {
            if (IsKeyword(kw))
            {
                _pos++;
                return true;
            }

            return false;
        }

        public void Expect(string kw)
        {
            if (!Eat(kw))
            {
                throw new TranslationException($"Expected {kw}{Found()}.");
            }
        }

        public bool EatOp(string op)
        {
            if (AtOp(op))
            {
                _pos++;
                return true;
            }

            return false;
        }

        public void ExpectOp(string op)
        {
            if (!EatOp(op))
            {
                throw new TranslationException($"Expected '{op}'{Found()}.");
            }
        }

        public void Move() => _pos++;

        public string Ident(string what)
        {
            var t = Peek;
            if (t is { Kind: TokKind.Ident, Quoted: true })
            {
                _pos++;
                return t.Text;
            }

            if (t is { Kind: TokKind.Ident, Quoted: false } && !Reserved.Contains(t.Text))
            {
                _pos++;
                return t.Text;
            }

            throw new TranslationException($"Expected {what}{Found()}.");
        }

        public string? OptionalAlias()
        {
            if (Eat("AS"))
            {
                var t = Peek;
                if (t is { Kind: TokKind.Str })
                {
                    _pos++;
                    return t.Text;
                }

                if (t is { Kind: TokKind.Ident, Quoted: true })
                {
                    _pos++;
                    return t.Text;
                }

                if (t is { Kind: TokKind.Ident, Quoted: false } && !Reserved.Contains(t.Text))
                {
                    _pos++;
                    return t.Text;
                }

                return null;
            }

            var p = Peek;
            if (p is { Kind: TokKind.Ident, Quoted: false } && !Reserved.Contains(p.Text))
            {
                _pos++;
                return p.Text;
            }

            return null;
        }

        public long Count(string what)
        {
            var parens = EatOp("(");
            if (Peek is { Kind: TokKind.Int } n && long.TryParse(n.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0)
            {
                _pos++;
                if (parens)
                {
                    ExpectOp(")");
                }

                return value;
            }

            throw new TranslationException($"Expected a non-negative integer for {what}{Found()}.");
        }

        public void Finish()
        {
            EatOp(";");
            if (Peek is not null)
            {
                throw new TranslationException($"Unexpected text after the statement{Found()}.");
            }
        }

        public Query ReadSelect()
        {
            var distinct = Eat("DISTINCT");
            long? limit = null;
            if (Eat("TOP"))
            {
                limit = Count("TOP");
            }

            var items = new List<SelectItem> { ReadSelectItem() };
            while (EatOp(","))
            {
                items.Add(ReadSelectItem());
            }

            if (Eat("INTO"))
            {
                throw new TranslationException("SELECT … INTO is not supported.");
            }

            Expect("FROM");
            var from = ReadTableRef();
            if (EatOp(","))
            {
                throw new TranslationException("Comma joins are not supported — use JOIN … ON.");
            }

            var joins = new List<JoinRef>();
            while (true)
            {
                var left = false;
                if (Eat("LEFT"))
                {
                    Eat("OUTER");
                    left = true;
                }
                else if (Eat("INNER"))
                {
                    left = false;
                }
                else if (IsKeyword("RIGHT") || IsKeyword("FULL") || IsKeyword("CROSS"))
                {
                    throw new TranslationException("Only INNER and LEFT joins are supported.");
                }
                else if (!IsKeyword("JOIN"))
                {
                    break;
                }

                Expect("JOIN");
                var table = ReadTableRef();
                Expect("ON");
                var on = ParseExpr();
                joins.Add(new JoinRef(left, table, on));
            }

            X? filter = null;
            if (Eat("WHERE"))
            {
                filter = ParseExpr();
            }

            var groupBy = new List<X>();
            if (Eat("GROUP"))
            {
                Expect("BY");
                groupBy.Add(ParseExpr());
                while (EatOp(","))
                {
                    groupBy.Add(ParseExpr());
                }
            }

            X? having = null;
            if (Eat("HAVING"))
            {
                having = ParseExpr();
            }

            var orderBy = new List<(X, bool)>();
            if (Eat("ORDER"))
            {
                Expect("BY");
                do
                {
                    var expr = ParseExpr();
                    var desc = false;
                    if (Eat("DESC"))
                    {
                        desc = true;
                    }
                    else
                    {
                        Eat("ASC");
                    }

                    orderBy.Add((expr, desc));
                }
                while (EatOp(","));
            }

            long? offset = null;
            while (true)
            {
                if (Eat("LIMIT"))
                {
                    if (limit is not null)
                    {
                        throw new TranslationException("Use either TOP or LIMIT, not both.");
                    }

                    limit = Count("LIMIT");
                }
                else if (Eat("OFFSET"))
                {
                    offset = Count("OFFSET");
                    if (!Eat("ROWS"))
                    {
                        Eat("ROW");
                    }
                }
                else if (Eat("FETCH"))
                {
                    if (!Eat("FIRST"))
                    {
                        Expect("NEXT");
                    }

                    if (limit is not null)
                    {
                        throw new TranslationException("Use only one of TOP, LIMIT or FETCH.");
                    }

                    limit = Count("FETCH");
                    if (!Eat("ROWS"))
                    {
                        Expect("ROW");
                    }

                    Expect("ONLY");
                }
                else
                {
                    break;
                }
            }

            if (IsKeyword("UNION") || IsKeyword("EXCEPT") || IsKeyword("INTERSECT"))
            {
                throw new TranslationException("UNION / EXCEPT / INTERSECT are not supported.");
            }

            Finish();
            return new Query(distinct, items, from, joins, filter, groupBy, having, orderBy, limit, offset);
        }

        private SelectItem ReadSelectItem()
        {
            if (EatOp("*"))
            {
                return new SelectItem(true, null, null);
            }

            if (Peek is { Kind: TokKind.Ident } a
                && _pos + 1 < tokens.Count && tokens[_pos + 1] is { Kind: TokKind.Op } dot && dot.Text == "."
                && _pos + 2 < tokens.Count && tokens[_pos + 2] is { Kind: TokKind.Op } star && star.Text == "*")
            {
                _pos += 3;
                return new SelectItem(true, null, null);
            }

            var expr = ParseExpr();
            var alias = OptionalAlias();
            return new SelectItem(false, expr, alias);
        }

        private TableRef ReadTableRef()
        {
            if (AtOp("("))
            {
                throw new TranslationException("Subqueries are not supported.");
            }

            var parts = new List<string> { Ident("a collection name") };
            while (EatOp("."))
            {
                parts.Add(Ident("a collection name"));
            }

            var alias = OptionalAlias();
            return new TableRef(string.Join(".", parts), alias);
        }

        // ------------------------------------------------------------------ expressions

        public X ParseExpr() => ParseOr();

        private X ParseOr()
        {
            var left = ParseAnd();
            while (Eat("OR"))
            {
                left = new Bin(BinOp.Or, left, ParseAnd());
            }

            return left;
        }

        private X ParseAnd()
        {
            var left = ParseNot();
            while (Eat("AND"))
            {
                left = new Bin(BinOp.And, left, ParseNot());
            }

            return left;
        }

        private X ParseNot()
        {
            if (Eat("NOT"))
            {
                return new Not(ParseNot());
            }

            return ParsePredicate();
        }

        private X ParsePredicate()
        {
            var left = ParseAdditive();

            if (Eat("IS"))
            {
                var nullNegated = Eat("NOT");
                Expect("NULL");
                return new IsNullX(left, nullNegated);
            }

            var negated = IsKeyword("NOT")
                ? TryEatNotModifier()
                : false;

            if (Eat("IN"))
            {
                ExpectOp("(");
                if (IsKeyword("SELECT"))
                {
                    throw new TranslationException("Subqueries are not supported.");
                }

                var list = new List<X> { ParseAdditive() };
                while (EatOp(","))
                {
                    list.Add(ParseAdditive());
                }

                ExpectOp(")");
                return new InX(left, list, negated);
            }

            if (Eat("LIKE"))
            {
                return new LikeX(left, ParseAdditive(), negated);
            }

            if (Eat("BETWEEN"))
            {
                var low = ParseAdditive();
                Expect("AND");
                var high = ParseAdditive();
                return new BetweenX(left, low, high, negated);
            }

            if (negated)
            {
                throw new TranslationException("Expected IN, LIKE or BETWEEN after NOT.");
            }

            BinOp op;
            if (EatOp("="))
            {
                op = BinOp.Eq;
            }
            else if (EatOp("<>") || EatOp("!="))
            {
                op = BinOp.Ne;
            }
            else if (EatOp("<="))
            {
                op = BinOp.Le;
            }
            else if (EatOp(">="))
            {
                op = BinOp.Ge;
            }
            else if (EatOp("<"))
            {
                op = BinOp.Lt;
            }
            else if (EatOp(">"))
            {
                op = BinOp.Gt;
            }
            else
            {
                return left;
            }

            return new Bin(op, left, ParseAdditive());
        }

        private bool TryEatNotModifier()
        {
            if (PeekAfter() is { Kind: TokKind.Ident, Quoted: false } nt
                && (nt.Text.Equals("IN", StringComparison.OrdinalIgnoreCase)
                    || nt.Text.Equals("LIKE", StringComparison.OrdinalIgnoreCase)
                    || nt.Text.Equals("BETWEEN", StringComparison.OrdinalIgnoreCase)))
            {
                Move(); // consume NOT, leaving IN / LIKE / BETWEEN for the caller's Eat().
                return true;
            }

            return false;
        }

        private X ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (true)
            {
                if (EatOp("+"))
                {
                    left = new Bin(BinOp.Add, left, ParseMultiplicative());
                }
                else if (EatOp("-"))
                {
                    left = new Bin(BinOp.Sub, left, ParseMultiplicative());
                }
                else
                {
                    break;
                }
            }

            return left;
        }

        private X ParseMultiplicative()
        {
            var left = ParseUnary();
            while (true)
            {
                BinOp op;
                if (EatOp("*"))
                {
                    op = BinOp.Mul;
                }
                else if (EatOp("/"))
                {
                    op = BinOp.Div;
                }
                else if (EatOp("%"))
                {
                    op = BinOp.Mod;
                }
                else
                {
                    break;
                }

                left = new Bin(op, left, ParseUnary());
            }

            return left;
        }

        private X ParseUnary()
        {
            if (EatOp("-"))
            {
                return ParseUnary() switch
                {
                    Lit { Kind: LitKind.Int } l => new Lit("-" + l.Text, LitKind.Int),
                    Lit { Kind: LitKind.Float } l => new Lit("-" + l.Text, LitKind.Float),
                    var other => new Neg(other),
                };
            }

            if (EatOp("+"))
            {
                return ParseUnary();
            }

            return ParsePrimary();
        }

        private X ParsePrimary()
        {
            var t = Peek ?? throw new TranslationException("Expected an expression but reached the end of the statement.");
            switch (t.Kind)
            {
                case TokKind.Int:
                    Move();
                    return new Lit(t.Text, LitKind.Int);
                case TokKind.Float:
                    Move();
                    return new Lit(t.Text, LitKind.Float);
                case TokKind.Str:
                    Move();
                    return new Lit(t.Text, LitKind.Str);
                case TokKind.Op when t.Text == "(":
                    Move();
                    if (IsKeyword("SELECT"))
                    {
                        throw new TranslationException("Subqueries are not supported.");
                    }

                    var inner = ParseExpr();
                    ExpectOp(")");
                    return inner;
                case TokKind.Op:
                    throw new TranslationException($"Unexpected '{t.Text}', expected an expression.");
                case TokKind.Ident:
                    if (!t.Quoted)
                    {
                        switch (t.Text.ToUpperInvariant())
                        {
                            case "NULL":
                                Move();
                                return new Lit("null", LitKind.Null);
                            case "TRUE":
                                Move();
                                return new Lit("true", LitKind.Bool);
                            case "FALSE":
                                Move();
                                return new Lit("false", LitKind.Bool);
                            case "DATE":
                            case "TIMESTAMP":
                            case "DATETIME":
                                if (PeekAfter() is { Kind: TokKind.Str } dt)
                                {
                                    Move();
                                    Move();
                                    return new Lit(dt.Text, LitKind.Date);
                                }

                                break;
                            case "SELECT":
                                throw new TranslationException("Subqueries are not supported.");
                        }
                    }

                    Move();
                    if (!t.Quoted && Peek is { Kind: TokKind.Op } f && f.Text == "(")
                    {
                        ExpectOp("(");
                        var distinct = Eat("DISTINCT");
                        var args = new List<X>();
                        var star = false;
                        if (EatOp("*"))
                        {
                            star = true;
                            ExpectOp(")");
                        }
                        else if (!EatOp(")"))
                        {
                            args.Add(ParseExpr());
                            while (EatOp(","))
                            {
                                args.Add(ParseExpr());
                            }

                            ExpectOp(")");
                        }

                        return new CallX(t.Text, args, distinct, star);
                    }

                    var parts = new List<string> { t.Text };
                    while (EatOp("."))
                    {
                        parts.Add(Ident("a field name"));
                    }

                    return new ColX(parts);
            }

            throw new TranslationException($"Unexpected token '{t.Text}'.");
        }

        private Token? PeekAfter() => _pos + 1 < tokens.Count ? tokens[_pos + 1] : null;
    }

    // ============================================================================
    // Name resolution (base collection + joins)
    // ============================================================================

    internal sealed record JoinScope(string Field, List<string> Names);

    internal sealed class Scope
    {
        private readonly List<string> _base;
        private readonly List<(string Field, List<string> Names)> _joins;

        public Scope(string collection, string? alias)
        {
            _base = new List<string> { collection };
            if (alias is not null)
            {
                _base.Add(alias);
            }

            _joins = [];
        }

        public Scope(Query q)
        {
            _base = new List<string> { q.From.Name };
            if (q.From.Alias is not null)
            {
                _base.Add(q.From.Alias);
            }

            _joins = q.Joins.Select(j => (JoinField(j), JoinNames(j))).ToList();
        }

        private static string JoinField(JoinRef j) => SanitizeName(j.Table.Alias ?? j.Table.Name);
        private static List<string> JoinNames(JoinRef j)
        {
            var names = new List<string> { j.Table.Name };
            if (j.Table.Alias is { } a)
            {
                names.Add(a);
            }

            return names;
        }

        public List<JoinScope> Joins
        {
            get
            {
                var items = new List<JoinScope>();
                foreach (var (fld, names) in _joins)
                {
                    items.Add(new JoinScope(fld, names));
                }

                return items;
            }
        }

        /// <summary>
        /// Resolves a dotted column reference to a document path. A single word is the field itself; a
        /// qualified word drops the base alias; a join alias becomes <c>field.rest</c>; anything else
        /// is a nested path (for example <c>address.city</c>).
        /// </summary>
        public string Path(List<string> parts)
        {
            if (parts.Count == 0)
            {
                return string.Empty;
            }

            if (parts.Count == 1)
            {
                return parts[0];
            }

            if (_base.Contains(parts[0]))
            {
                return string.Join(".", parts.Skip(1));
            }

            foreach (var (field, names) in _joins)
            {
                if (names.Contains(parts[0]))
                {
                    return field + "." + string.Join(".", parts.Skip(1));
                }
            }

            return string.Join(".", parts);
        }
    }

    // ============================================================================
    // Expression compiler: aggregation operators and query filters
    // ============================================================================

    /// <summary>
    /// Turns a parsed expression into either a query-language filter (where possible, so indexes stay
    /// usable) or a <c>$expr</c> aggregation expression. Holds a scope and an optional group context.
    /// </summary>
    internal sealed class ExprCompiler(Scope scope, GroupMap? group, IReadOnlyDictionary<string, string>? fieldTypes = null)
    {
        public string Path(List<string> parts) => scope.Path(parts);

        /// <summary>
        /// Coerces a string literal compared with a known-typed field. MongoDB compares BSON types
        /// strictly, so <c>_id = 'guid'</c> can never match a UUID binary; the sampled schema lets us
        /// build the right value instead of forcing the user to spell <c>UUID('…')</c>.
        /// </summary>
        private BsonValue Coerce(string path, BsonValue value)
        {
            if (fieldTypes is null || value is not BsonString text)
            {
                return value;
            }

            return FieldType(path) switch
            {
                "uuid" when Guid.TryParse(text.Value, out var guid) =>
                    new BsonBinaryData(guid.ToByteArray(bigEndian: true), BsonBinarySubType.UuidStandard),
                "objectid" when ObjectId.TryParse(text.Value, out var id) => new BsonObjectId(id),
                _ => value,
            };
        }

        private string? FieldType(string path)
        {
            if (fieldTypes!.TryGetValue(path, out var type))
            {
                return type;
            }

            // A bare field reference in the query may differ from the sample's dotted path.
            var last = path.LastIndexOf('.');
            return last >= 0 && fieldTypes.TryGetValue(path[(last + 1)..], out var bare) ? bare : null;
        }

        /// <summary>A reference for a group key or aggregate after a $group stage, else null.</summary>
        public static string? GroupRef(GroupMap? group, X x)
        {
            if (group is null)
            {
                return null;
            }

            for (var index = 0; index < group.Keys.Count; index++)
            {
                if (XEq(group.Keys[index], x))
                {
                    return group.IdIsObject ? "$_id." + group.KeyNames[index] : "$_id";
                }
            }

            for (var index = 0; index < group.Aggregates.Count; index++)
            {
                if (XEq(group.Aggregates[index], x))
                {
                    return "$" + group.AggNames[index];
                }
            }

            return null;
        }

        // ------------------------------------------------------------- aggregation expression

        public BsonValue Expr(X x)
        {
            if (group is not null)
            {
                var g = GroupRef(group, x);
                if (g is not null)
                {
                    return BsonString.Create(g);
                }

                if (x is ColX)
                {
                    throw new TranslationException("A column in this position must appear in GROUP BY or inside an aggregate such as COUNT() or SUM().");
                }
            }

            if (TryConstant(x, out var constant))
            {
                return LiteralExpr(constant);
            }

            return x switch
            {
                ColX { Parts: var parts } => BsonString.Create("$" + scope.Path(parts)),
                Neg n => new BsonDocument("$multiply", new BsonArray { Expr(n.Inner), -1 }),
                Not n => new BsonDocument("$not", new BsonArray { Expr(n.Inner) }),
                Bin { Op: var op, L: var l, R: var r } => Expr(l, r, op),
                IsNullX { Inner: var inn, Negated: var negated } => new BsonDocument(negated ? "$gt" : "$lte", new BsonArray { Expr(inn), BsonNull.Value }),
                InX { Inner: var ii, List: var list, Negated: var negated } => InExprValue(ii, list, negated),
                BetweenX { Inner: var bi, Low: var lo, High: var hi, Negated: var bn } => BetweenExpr(bi, lo, hi, bn),
                LikeX { Inner: var li, Pattern: var pa, Negated: var ln } => LikeExpr(li, pa, ln),
                CallX c => FunctionExpr(c),
                Lit lit => throw new TranslationException("Internal: literal not converted."),
                _ => throw new TranslationException("Internal: unsupported expression."),
            };
        }

        private BsonValue Expr(X l, X r, BinOp op)
        {
            var name = op switch
            {
                BinOp.And => "$and",
                BinOp.Or => "$or",
                BinOp.Eq => "$eq",
                BinOp.Ne => "$ne",
                BinOp.Lt => "$lt",
                BinOp.Le => "$lte",
                BinOp.Gt => "$gt",
                BinOp.Ge => "$gte",
                BinOp.Add => "$add",
                BinOp.Sub => "$subtract",
                BinOp.Mul => "$multiply",
                BinOp.Div => "$divide",
                BinOp.Mod => "$mod",
                _ => throw new TranslationException("Unsupported operator in this expression."),
            };

            return new BsonDocument(name, new BsonArray { Expr(l), Expr(r) });
        }

        private BsonValue BetweenExpr(X inner, X low, X high, bool negated)
        {
            var test = new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("$gte", new BsonArray { Expr(inner), Expr(low) }),
                new BsonDocument("$lte", new BsonArray { Expr(inner), Expr(high) }),
            });
            return negated ? new BsonDocument("$not", new BsonArray { test }) : test;
        }

        private BsonValue InExprValue(X inner, List<X> list, bool negated)
        {
            var test = new BsonDocument("$in", new BsonArray
            {
                Expr(inner),
                new BsonArray(list.Select(Expr)),
            });
            return negated ? new BsonDocument("$not", new BsonArray { test }) : test;
        }

        private BsonValue LikeExpr(X inner, X pattern, bool negated)
        {
            if (pattern is not Lit { Kind: LitKind.Str } pat)
            {
                throw new TranslationException("LIKE needs a string pattern.");
            }

            var test = new BsonDocument("$regexMatch", new BsonDocument
            {
                ["input"] = Expr(inner),
                ["regex"] = new BsonRegularExpression(LikePattern(pat.Text), "s"),
            });
            return negated ? new BsonDocument("$not", new BsonArray { test }) : test;
        }

        private BsonValue FunctionExpr(CallX call)
        {
            var name = call.Name.ToUpperInvariant();
            if (Aggregates.Contains(name))
            {
                throw new TranslationException($"{name}() is an aggregate and is not allowed inside another expression.");
            }

            if (call.Star)
            {
                throw new TranslationException($"{name}() does not take *.");
            }

            var args = call.Args;
            var compiled = args.Select(Expr).ToList();

            BsonValue One(string op, int count = 1)
            {
                if (compiled.Count != count)
                {
                    throw new TranslationException($"{name}() takes {count} argument(s).");
                }

                return new BsonDocument(op, compiled[0]);
            }

            BsonValue ZeroNow()
            {
                if (compiled.Count != 0)
                {
                    throw new TranslationException($"{name}() takes no arguments.");
                }

                return BsonString.Create("$$NOW");
            }

            return name switch
            {
                "UPPER" => One("$toUpper"),
                "LOWER" => One("$toLower"),
                "LEN" or "LENGTH" => One("$strLenCP"),
                "ABS" => One("$abs"),
                "CEILING" or "CEIL" => One("$ceil"),
                "FLOOR" => One("$floor"),
                "SQRT" => One("$sqrt"),
                "ROUND" => compiled.Count switch
                {
                    1 => new BsonDocument("$round", new BsonArray { compiled[0] }),
                    2 => new BsonDocument("$round", new BsonArray { compiled[0], compiled[1] }),
                    _ => throw new TranslationException("ROUND() takes one or two arguments."),
                },
                "YEAR" => One("$year"),
                "MONTH" => One("$month"),
                "DAY" => One("$dayOfMonth"),
                "HOUR" => One("$hour"),
                "MINUTE" => One("$minute"),
                "SECOND" => One("$second"),
                "TRIM" => new BsonDocument("$trim", new BsonDocument("input", compiled[0])),
                "LTRIM" => new BsonDocument("$ltrim", new BsonDocument("input", compiled[0])),
                "RTRIM" => new BsonDocument("$rtrim", new BsonDocument("input", compiled[0])),
                "CONCAT" => new BsonDocument("$concat", new BsonArray(compiled)),
                "COALESCE" or "ISNULL" => new BsonDocument("$ifNull", new BsonArray(compiled)),
                "NOW" or "GETDATE" or "GETUTCDATE" or "SYSDATETIME" or "SYSDATETIMEOFFSET" => ZeroNow(),
                "POWER" => new BsonDocument("$pow", new BsonArray(compiled)),
                "SUBSTRING" or "SUBSTR" => Substring(compiled),
                _ => throw new TranslationException($"The function {call.Name}() is not supported."),
            };
        }

        private static BsonValue Substring(List<BsonValue> args)
        {
            if (args.Count != 3)
            {
                throw new TranslationException("SUBSTRING(string, start, length) takes 3 arguments.");
            }

            // SQL is 1-based; $substrCP is 0-based.
            BsonValue start = args[1] switch
            {
                BsonInt32 n => BsonInt32.Create(n.Value - 1),
                BsonInt64 n => BsonInt64.Create(n.Value - 1),
                var other => new BsonDocument("$subtract", new BsonArray { other, 1 }),
            };
            return new BsonDocument("$substrCP", new BsonArray { args[0], start, args[2] });
        }

        // ------------------------------------------------------------- query-language filter

        /// <summary>
        /// A query-language filter for <paramref name="x"/>, or null when it must fall back to
        /// <c>$expr</c>.
        /// </summary>
        public BsonDocument? Filter(X x)
        {
            switch (x)
            {
                case Bin { Op: BinOp.And, L: var l, R: var r }:
                    var fl = Filter(l);
                    var fr = Filter(r);
                    if (fl is null || fr is null)
                    {
                        return null;
                    }

                    return new BsonDocument("$and", new BsonArray { fl, fr });
                case Bin { Op: BinOp.Or, L: var lo, R: var ro }:
                    var fo = Filter(lo);
                    var fro = Filter(ro);
                    if (fo is null || fro is null)
                    {
                        return null;
                    }

                    return new BsonDocument("$or", new BsonArray { fo, fro });
                case Not n:
                    return Filter(n.Inner) is { } fin ? new BsonDocument("$nor", new BsonArray { fin }) : null;
                case IsNullX { Inner: ColX c, Negated: var negated }:
                    return new BsonDocument(scope.Path(c.Parts), negated ? new BsonDocument("$ne", BsonNull.Value) : BsonNull.Value);
                case InX { Inner: ColX c, List: var list, Negated: var negated }:
                    var inPath = scope.Path(c.Parts);
                    var inVals = new BsonArray();
                    foreach (var item in list)
                    {
                        if (!TryConstant(item, out var v))
                        {
                            return null;
                        }

                        inVals.Add(Coerce(inPath, v));
                    }

                    return new BsonDocument(inPath, new BsonDocument(negated ? "$nin" : "$in", inVals));
                case BetweenX { Inner: ColX c, Low: var low, High: var high, Negated: var negated }:
                    if (!TryConstant(low, out var lv) || !TryConstant(high, out var hv))
                    {
                        return null;
                    }

                    var path = scope.Path(c.Parts);
                    if (negated)
                    {
                        return new BsonDocument("$or", new BsonArray
                        {
                            new BsonDocument(path, new BsonDocument("$lt", lv)),
                            new BsonDocument(path, new BsonDocument("$gt", hv)),
                        });
                    }

                    return new BsonDocument(path, new BsonDocument { ["$gte"] = lv, ["$lte"] = hv });
                case LikeX { Inner: ColX c, Pattern: Lit { Kind: LitKind.Str } p, Negated: var negated }:
                    return negated
                        ? new BsonDocument(scope.Path(c.Parts), new BsonDocument("$not", new BsonRegularExpression(LikePattern(p.Text), "s")))
                        : new BsonDocument(scope.Path(c.Parts), new BsonRegularExpression(LikePattern(p.Text), "s"));
                case Bin { Op: var op } bin when IsComparison(op):
                    var baseCol = bin.L as ColX;
                    var other = bin.R;
                    var flipped = false;
                    if (baseCol is null && bin.R is ColX rc)
                    {
                        baseCol = rc;
                        other = bin.L;
                        flipped = true;
                    }

                    if (baseCol is null || !TryConstant(other, out var value))
                    {
                        return null;
                    }

                    var fieldPath = scope.Path(baseCol.Parts);
                    value = Coerce(fieldPath, value);
                    if (op == BinOp.Eq && !value.IsBsonDocument && value.BsonType != BsonType.RegularExpression)
                    {
                        return new BsonDocument(fieldPath, value);
                    }

                    var qop = flipped ? Flip(op) : op;
                    return new BsonDocument(fieldPath, new BsonDocument(OpName(qop), value));
                case ColX bare:
                    return new BsonDocument(scope.Path(bare.Parts), true);
                default:
                    return null;
            }
        }

        private static bool IsComparison(BinOp op) => op is BinOp.Eq or BinOp.Ne or BinOp.Lt or BinOp.Le or BinOp.Gt or BinOp.Ge;

        private static string OpName(BinOp op) => op switch
        {
            BinOp.Eq => "$eq",
            BinOp.Ne => "$ne",
            BinOp.Lt => "$lt",
            BinOp.Le => "$lte",
            BinOp.Gt => "$gt",
            BinOp.Ge => "$gte",
            _ => throw new TranslationException("Unsupported comparison."),
        };

        private static BinOp Flip(BinOp op) => op switch
        {
            BinOp.Lt => BinOp.Gt,
            BinOp.Le => BinOp.Ge,
            BinOp.Gt => BinOp.Lt,
            BinOp.Ge => BinOp.Le,
            _ => op,
        };
    }

    // ============================================================================
    // SELECT compiler
    // ============================================================================

    internal sealed record GroupMap(
        BsonDocument Stage,
        List<X> Keys,
        List<string> KeyNames,
        List<CallX> Aggregates,
        List<string> AggNames,
        bool IdIsObject);

    internal sealed class SelectCompiler(IReadOnlyDictionary<string, string>? fieldTypes)
    {
        public MSelect Compile(Query q, string src)
        {
            var scope = new Scope(q);
            var keys = q.GroupBy.Count > 0
                ? q.GroupBy
                : q.Distinct ? q.Items.Where(i => i.Value is not null).Select(i => i.Value!).ToList() : [];
            var hasAgg = HasAggregate(q.Items) || HasAggregate(q.Having) || q.OrderBy.Any(o => HasAggregate(o.Expr));
            var doGroup = q.GroupBy.Count > 0 || hasAgg || q.Distinct;

            var groupMap = doGroup ? BuildGroup(q, keys, fieldTypes) : null;
            var compiler = new ExprCompiler(scope, groupMap, fieldTypes);
            var pipeline = new List<BsonDocument>();

            if (q.Filter is { } filter)
            {
                pipeline.Add(MatchFilter(compiler, filter));
            }

            foreach (var join in q.Joins)
            {
                AddJoin(q, join, pipeline);
            }

            if (groupMap is not null)
            {
                pipeline.Add(new BsonDocument("$group", groupMap.Stage));
            }

            if (q.Having is { } having)
            {
                pipeline.Add(MatchFilter(compiler, having));
            }

            if (q.OrderBy.Count > 0)
            {
                pipeline.Add(SortStage(scope, groupMap, q.OrderBy));
            }

            if (q.Offset is { } skip)
            {
                pipeline.Add(new BsonDocument("$skip", new BsonInt32((int)skip)));
            }

            if (q.Limit is { } take)
            {
                pipeline.Add(new BsonDocument("$limit", new BsonInt32((int)take)));
            }

            var (project, columns) = Project(scope, groupMap, q, fieldTypes);
            if (project is not null)
            {
                pipeline.Add(project);
            }

            return new MSelect(q.From.Name, pipeline, columns, src);
        }

        private static BsonDocument MatchFilter(ExprCompiler compiler, X expr)
        {
            var filter = compiler.Filter(expr);
            if (filter is not null)
            {
                return new BsonDocument("$match", filter);
            }

            var conjuncts = new List<X>();
            SplitAnd(expr, conjuncts);
            if (conjuncts.Count > 1)
            {
                var parts = new BsonArray();
                foreach (var c in conjuncts)
                {
                    parts.Add(compiler.Filter(c) ?? new BsonDocument("$expr", compiler.Expr(c)));
                }

                return new BsonDocument("$match", new BsonDocument("$and", parts));
            }

            return new BsonDocument("$match", new BsonDocument("$expr", compiler.Expr(expr)));
        }

        private static void SplitAnd(X x, List<X> outConjuncts)
        {
            if (x is Bin { Op: BinOp.And, L: var l, R: var r })
            {
                SplitAnd(l, outConjuncts);
                SplitAnd(r, outConjuncts);
            }
            else
            {
                outConjuncts.Add(x);
            }
        }

        private static void AddJoin(Query q, JoinRef join, List<BsonDocument> pipeline)
        {
            // A single-column equality is the supported ON shape.
            var scope = new Scope(q);
            var (local, foreign) = JoinKeys(join, scope);
            var field = SanitizeName(join.Table.Alias ?? join.Table.Name);
            pipeline.Add(new BsonDocument
            {
                ["$lookup"] = new BsonDocument
                {
                    ["from"] = join.Table.Name,
                    ["localField"] = local,
                    ["foreignField"] = foreign,
                    ["as"] = field,
                },
            });
            pipeline.Add(new BsonDocument
            {
                ["$unwind"] = new BsonDocument
                {
                    ["path"] = "$" + field,
                    ["preserveNullAndEmptyArrays"] = join.Left,
                },
            });
        }

        private static (string Local, string Foreign) JoinKeys(JoinRef join, Scope scope)
        {
            if (join.On is not Bin { Op: BinOp.Eq, L: ColX l, R: ColX r })
            {
                throw new TranslationException("JOIN … ON must be a single column equality (a.x = b.y).");
            }

            var joinNames = new List<string> { join.Table.Name };
            if (join.Table.Alias is { } a)
            {
                joinNames.Add(a);
            }

            var lLocal = l.Parts.Count > 1 && joinNames.Contains(l.Parts[0]) ? string.Join(".", l.Parts.Skip(1)) : scope.Path(l.Parts);
            var rForeign = r.Parts.Count > 1 && joinNames.Contains(r.Parts[0]) ? string.Join(".", r.Parts.Skip(1)) : scope.Path(r.Parts);

            // Element 0 of scope.Path strips the base alias; the join-side path must stay scoped to
            // the joined collection's own fields.
            var baseNames = new List<string> { /* base collection name/alias already stripped */ };
            _ = baseNames;

            // Deduce which side belongs to the base vs the join by their first word.
            var baseSide = joinNames.Contains(l.Parts[0]) ? r : l;
            var joinSide = joinNames.Contains(l.Parts[0]) ? l : r;
            var localBase = scope.Path(baseSide.Parts);
            var joinField = joinSide.Parts.Count > 1 ? string.Join(".", joinSide.Parts.Skip(1)) : joinSide.Parts[0];
            return (localBase, joinField);
        }

        private static GroupMap BuildGroup(Query q, List<X> keys, IReadOnlyDictionary<string, string>? fieldTypes)
        {
            var stage = new BsonDocument();
            var keyNames = new List<string>();
            var used = new HashSet<string>(StringComparer.Ordinal);
            var scope = new Scope(q);
            var id = new BsonDocument();

            for (var index = 0; index < keys.Count; index++)
            {
                string name;
                if (keys[index] is not ColX { Parts: var parts })
                {
                    throw new TranslationException("GROUP BY must name a column.");
                }

                name = SanitizeName(string.Join("_", parts));
                var unique = Unique(name, used);
                keyNames.Add(unique);
                id[unique] = BsonString.Create("$" + scope.Path(parts));
            }

            stage["_id"] = id.ElementCount == 0 ? BsonNull.Value : id.ElementCount == 1 ? id.ElementAt(0).Value : id;

            var aggregates = new List<CallX>();
            var aggNames = new List<string>();
            var aggCompiler = new ExprCompiler(scope, null, fieldTypes);
            foreach (var item in q.Items)
            {
                if (item.Value is { } v)
                {
                    CollectAggregates(v, aggCompiler, aggregates, aggNames, used);
                }
            }

            foreach (var (oexpr, _) in q.OrderBy)
            {
                CollectAggregates(oexpr, aggCompiler, aggregates, aggNames, used);
            }

            for (var index = 0; index < aggregates.Count; index++)
            {
                stage[aggNames[index]] = Accumulator(aggregates[index], aggCompiler, aggNames[index]);
            }

            return new GroupMap(stage, keys, keyNames, aggregates, aggNames, id.ElementCount > 1);
        }

        private static void CollectAggregates(X x, ExprCompiler compiler, List<CallX> aggregates, List<string> aggNames, HashSet<string> used)
        {
            Walk(x, e =>
            {
                if (e is CallX { Name: var name } call && Aggregates.Contains(name) && !aggregates.Any(a => XEq(a, call)))
                {
                    aggregates.Add(call);
                    aggNames.Add(Unique(SanitizeName(call.Name.ToLowerInvariant()), used));
                }
            });
        }

        private static BsonValue Accumulator(CallX call, ExprCompiler compiler, string aggName)
        {
            var name = call.Name.ToUpperInvariant();
            if (name == "COUNT" && call.Star)
            {
                return new BsonDocument("$sum", 1);
            }

            if (call.Args.Count != 1)
            {
                throw new TranslationException($"{name}() takes one argument.");
            }

            var arg = compiler.Expr(call.Args[0]);
            return name switch
            {
                "COUNT" when call.Distinct => new BsonDocument("$addToSet", arg),
                "COUNT" => new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { new BsonDocument("$gt", new BsonArray { arg, BsonNull.Value }), 1, 0 })),
                "SUM" => new BsonDocument("$sum", arg),
                "AVG" => new BsonDocument("$avg", arg),
                "MIN" => new BsonDocument("$min", arg),
                "MAX" => new BsonDocument("$max", arg),
                _ => throw new TranslationException($"The aggregate {name}() is not supported."),
            };
        }

        private static BsonDocument SortStage(Scope scope, GroupMap? group, List<(X, bool)> orderBy)
        {
            var sort = new BsonDocument();
            foreach (var (oexpr, desc) in orderBy)
            {
                string field;
                if (group is not null)
                {
                    var gref = ExprCompiler.GroupRef(group, oexpr);
                    if (gref is not null)
                    {
                        field = gref.StartsWith("$_id.")
                            ? "_id." + gref[5..]
                            : gref[1..];
                    }
                    else if (oexpr is ColX)
                    {
                        throw new TranslationException("A column in ORDER BY must appear in GROUP BY or inside an aggregate.");
                    }
                    else
                    {
                        throw new TranslationException("ORDER BY in a grouped query must name a group key or aggregate.");
                    }
                }
                else
                {
                    if (oexpr is not ColX { Parts: var parts })
                    {
                        throw new TranslationException("ORDER BY outside a group must name a plain column.");
                    }

                    field = scope.Path(parts);
                }

                sort[field] = desc ? -1 : 1;
            }

            return new BsonDocument("$sort", sort);
        }

        private static (BsonDocument? Project, IReadOnlyList<string>? Columns) Project(Scope scope, GroupMap? group, Query q, IReadOnlyDictionary<string, string>? fieldTypes)
        {
            var wildcardOnly = q.Items.Count == 1 && q.Items[0].Wildcard;
            if (q.Items.Any(i => i.Wildcard) && !wildcardOnly)
            {
                throw new TranslationException("Mixing * with other columns is not supported — name the columns.");
            }

            if (wildcardOnly)
            {
                if (q.Distinct)
                {
                    throw new TranslationException("SELECT DISTINCT * is not supported — name the columns.");
                }

                if (group is not null)
                {
                    throw new TranslationException("A grouped query needs an explicit SELECT column list.");
                }

                return (null, null);
            }

            var compiler = new ExprCompiler(scope, group, fieldTypes);
            var projection = new BsonDocument { ["_id"] = 0 };
            var columns = new List<string>();
            var used = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in q.Items)
            {
                var value = item.Value!;
                var outName = Unique(SanitizeName(item.Alias ?? DefaultName(value)), used);
                var expr = group is not null && ExprCompiler.GroupRef(group, value) is { } g
                    ? BsonString.Create(g)
                    : compiler.Expr(value);
                projection[outName] = expr;
                columns.Add(outName);
            }

            return (new BsonDocument("$project", projection), columns);
        }

        private static string DefaultName(X x) => x switch
        {
            ColX { Parts: var p } => p[^1],
            CallX { Name: var n, Star: true } => n.ToLowerInvariant(),
            CallX { Name: var n2, Args: var args } when args.Count > 0 && args[0] is ColX { Parts: var pc } => $"{n2.ToLowerInvariant()}_{string.Join("_", pc)}",
            CallX { Name: var n3 } => n3.ToLowerInvariant(),
            _ => "expr",
        };

        private static bool HasAggregate(X? x)
        {
            if (x is null)
            {
                return false;
            }

            var found = false;
            Walk(x, e => { if (e is CallX c && Aggregates.Contains(c.Name)) found = true; });
            return found;
        }

        private static bool HasAggregate(List<SelectItem> items) => items.Any(i => i.Value is not null && HasAggregate(i.Value));

        private static void Walk(X x, Action<X> visit)
        {
            visit(x);
            switch (x)
            {
                case Neg n:
                    Walk(n.Inner, visit);
                    break;
                case Not n:
                    Walk(n.Inner, visit);
                    break;
                case Bin { L: var l, R: var r }:
                    Walk(l, visit);
                    Walk(r, visit);
                    break;
                case IsNullX { Inner: var inn }:
                    Walk(inn, visit);
                    break;
                case InX { Inner: var ii, List: var list }:
                    Walk(ii, visit);
                    foreach (var item in list)
                    {
                        Walk(item, visit);
                    }

                    break;
                case BetweenX { Inner: var bi, Low: var lo, High: var hi }:
                    Walk(bi, visit);
                    Walk(lo, visit);
                    Walk(hi, visit);
                    break;
                case LikeX { Inner: var li, Pattern: var pa }:
                    Walk(li, visit);
                    Walk(pa, visit);
                    break;
                case CallX { Args: var cs }:
                    foreach (var a in cs)
                    {
                        Walk(a, visit);
                    }

                    break;
                default:
                    break;
            }
        }

        private static string Unique(string name, HashSet<string> used)
        {
            var candidate = name;
            var n = 2;
            while (!used.Add(candidate))
            {
                candidate = $"{name}_{n}";
                n++;
            }

            return candidate;
        }
    }

    // ============================================================================
    // INSERT / UPDATE / DELETE
    // ============================================================================

    private static MongoCommand ReadInsert(Reader p, string src)
    {
        p.Expect("INTO");
        var collection = p.Ident("a collection name");

        var columns = new List<string>();
        if (p.EatOp("("))
        {
            columns.Add(p.Ident("a column name"));
            while (p.EatOp(","))
            {
                columns.Add(p.Ident("a column name"));
            }

            p.ExpectOp(")");
        }

        p.Expect("VALUES");
        var documents = new List<BsonDocument>();
        do
        {
            p.ExpectOp("(");
            var values = new List<X>();
            if (!p.EatOp(")"))
            {
                values.Add(p.ParseExpr());
                while (p.EatOp(","))
                {
                    values.Add(p.ParseExpr());
                }

                p.ExpectOp(")");
            }

            if (columns.Count > 0)
            {
                if (values.Count != columns.Count)
                {
                    throw new TranslationException($"INSERT has {values.Count} value(s) but {columns.Count} column(s).");
                }

                var doc = new BsonDocument();
                for (var index = 0; index < columns.Count; index++)
                {
                    doc[columns[index]] = ValueToBson(values[index]);
                }

                documents.Add(doc);
            }
            else
            {
                // No column list: the values are stored under generated keys. Use the column list —
                // INSERTS with named fields — for documents that read naturally.
                var doc = new BsonDocument();
                for (var index = 0; index < values.Count; index++)
                {
                    doc[$"c{index + 1}"] = ValueToBson(values[index]);
                }

                documents.Add(doc);
            }
        }
        while (p.EatOp(","));

        p.Finish();
        return new MInsert(collection, documents, src);
    }

    private static MongoCommand ReadUpdate(Reader p, string src, IReadOnlyDictionary<string, string>? fieldTypes)
    {
        var collection = p.Ident("a collection name");
        p.Expect("SET");
        var updates = new BsonDocument();
        do
        {
            var col = p.Ident("a column name");
            p.ExpectOp("=");
            updates[col] = ValueToBson(p.ParseExpr());
        }
        while (p.EatOp(","));

        X? where = null;
        if (p.Eat("WHERE"))
        {
            where = p.ParseExpr();
        }

        p.Finish();
        var compiler = new ExprCompiler(new Scope(collection, null), null, fieldTypes);
        var filter = where is null
            ? new BsonDocument()
            : compiler.Filter(where) ?? new BsonDocument("$expr", compiler.Expr(where));
        return new MUpdate(collection, filter, updates, true, src);
    }

    private static MongoCommand ReadDelete(Reader p, string src, IReadOnlyDictionary<string, string>? fieldTypes)
    {
        p.Expect("FROM");
        var collection = p.Ident("a collection name");
        X? where = null;
        if (p.Eat("WHERE"))
        {
            where = p.ParseExpr();
        }

        p.Finish();
        var compiler = new ExprCompiler(new Scope(collection, null), null, fieldTypes);
        var filter = where is null
            ? new BsonDocument()
            : compiler.Filter(where) ?? new BsonDocument("$expr", compiler.Expr(where));
        return new MDelete(collection, filter, true, src);
    }

    // ============================================================================
    // Literals and shared helpers
    // ============================================================================

    private static BsonValue ValueToBson(X x)
    {
        switch (x)
        {
            case Lit { Kind: LitKind.Null }:
                return BsonNull.Value;
            case Lit { Kind: LitKind.Bool } b:
                return BsonBoolean.Create(b.Text.Equals("true", StringComparison.OrdinalIgnoreCase));
            case Lit { Kind: LitKind.Int } i:
                return long.TryParse(i.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv)
                    ? iv is >= int.MinValue and <= int.MaxValue ? BsonInt32.Create((int)iv) : BsonInt64.Create(iv)
                    : BsonString.Create(i.Text);
            case Lit { Kind: LitKind.Float } f:
                return double.TryParse(f.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fv)
                    ? BsonDouble.Create(fv)
                    : BsonString.Create(f.Text);
            case Lit { Kind: LitKind.Str } s:
                return BsonString.Create(s.Text);
            case Lit { Kind: LitKind.Date } d:
                return BsonDateTime.Create(BsonValues.ParseDate(d.Text));
            case Neg n:
                return ValueToBson(n.Inner) switch
                {
                    BsonInt32 v => BsonInt32.Create(-v.Value),
                    BsonInt64 v => BsonInt64.Create(-v.Value),
                    BsonDouble v => BsonDouble.Create(-v.Value),
                    var other => new BsonDocument("$multiply", new BsonArray { other, -1 }),
                };
            case CallX c:
                return BsonValues.HelperToBson(c.Name, c.Args);
            default:
                throw new TranslationException("INSERT/UPDATE values must be literals or shell helpers, not columns.");
        }
    }

    private static bool TryConstant(X x, out BsonValue value)
    {
        value = BsonNull.Value;
        switch (x)
        {
            case Lit { Kind: LitKind.Null }:
                return true;
            case Lit { Kind: LitKind.Bool } b:
                value = BsonBoolean.Create(b.Text.Equals("true", StringComparison.OrdinalIgnoreCase));
                return true;
            case Lit { Kind: LitKind.Int } i:
                if (long.TryParse(i.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv))
                {
                    value = iv is >= int.MinValue and <= int.MaxValue ? BsonInt32.Create((int)iv) : BsonInt64.Create(iv);
                    return true;
                }

                return false;
            case Lit { Kind: LitKind.Float } f:
                if (double.TryParse(f.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fv))
                {
                    value = BsonDouble.Create(fv);
                    return true;
                }

                return false;
            case Lit { Kind: LitKind.Str } s:
                value = BsonString.Create(s.Text);
                return true;
            case Lit { Kind: LitKind.Date } d:
                value = BsonDateTime.Create(BsonValues.ParseDate(d.Text));
                return true;
            case CallX c:
                try
                {
                    value = BsonValues.HelperToBson(c.Name, c.Args);
                    return true;
                }
                catch (TranslationException)
                {
                    return false;
                }

            default:
                return false;
        }
    }

    private static BsonValue LiteralExpr(BsonValue value) => value switch
    {
        BsonString { Value: var s } when s.StartsWith('$') => new BsonDocument("$literal", value),
        BsonArray or BsonDocument => new BsonDocument("$literal", value),
        _ => value,
    };

    private static string LikePattern(string pattern)
    {
        var re = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            re.Append(c switch
            {
                '%' => ".*",
                '_' => ".",
                _ => RegexEscape(c),
            });
        }

        re.Append('$');
        return re.ToString();
    }

    private static string RegexEscape(char c) => @"\^$.|?*+()[]{}/".Contains(c) ? "\\" + c : c.ToString();

    /// <summary>Shared BSON value helpers for literal dates and shell helpers.</summary>
    internal static class BsonValues
    {
        public static DateTime ParseDate(string text)
        {
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            {
                return dt;
            }

            throw new TranslationException($"Unrecognised date '{text}' — use 'yyyy-MM-dd' or 'yyyy-MM-ddTHH:mm:ss'.");
        }

        public static BsonValue HelperToBson(string name, List<X> args)
        {
            var upper = name.ToUpperInvariant();
            if (args.Count != 1)
            {
                throw new TranslationException($"{name}() needs a single literal argument.");
            }

            var arg = args[0];
            return upper switch
            {
                "OBJECTID" => new BsonObjectId(
                    ObjectId.TryParse((arg as Lit)?.Text, out var id)
                        ? id
                        : throw new TranslationException("ObjectId('…') needs a 24-character hex string.")),
                "ISODATE" or "DATE" or "DATETIME" => BsonDateTime.Create(ParseDate(StrOf(arg))),
                "UUID" or "GUID" => Guid.TryParse(StrOf(arg), out var guid)
                    ? new BsonBinaryData(guid.ToByteArray(bigEndian: true), BsonBinarySubType.UuidStandard)
                    : throw new TranslationException("UUID('…') needs a GUID such as 'f903436c-37b7-4b41-87fd-791fa6739cd6'."),
                "NUMBERINT" or "NUMBERLONG" => LongOr(arg),
                "NUMBERDECIMAL" => decimal.TryParse(StrOf(arg).Trim('"'), NumberStyles.Number, CultureInfo.InvariantCulture, out var dec)
                    ? BsonDecimal128.Create(dec)
                    : throw new TranslationException("NumberDecimal('…') needs a number."),
                "TIMESTAMP" => BsonDateTime.Create(ParseDate(StrOf(arg))),
                _ => throw new TranslationException($"The shell helper {name}() is not supported here."),
            };
        }

        private static string StrOf(X x) => x switch
        {
            Lit { Kind: LitKind.Str } s => s.Text,
            Lit { Kind: LitKind.Int } i => i.Text,
            Lit { Kind: LitKind.Float } f => f.Text,
            _ => throw new TranslationException("Expected a literal argument."),
        };

        private static BsonValue LongOr(X x)
        {
            var text = StrOf(x).Trim('"');
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv)
                ? BsonInt64.Create(iv)
                : throw new TranslationException("NumberLong('…') needs an integer.");
        }
    }
}