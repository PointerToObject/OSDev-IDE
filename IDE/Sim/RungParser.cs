using System;
using System.Collections.Generic;
using System.Text;

namespace OSDevIDE.Sim
{
    /// <summary>
    /// Parses Logix rung text like
    ///     XIC(Start)[XIC(Run),XIC(Manual)]XIO(Stop)OTE(Motor);
    /// into a tree of <see cref="RungItem"/> nodes that the VM can evaluate.
    ///
    /// Grammar:
    ///     rung    := items ';'
    ///     items   := (instr | branch)*
    ///     instr   := NAME '(' args? ')'
    ///     args    := arg (',' arg)*        — args may be tags, members, indices,
    ///                                        literals, or sub-expressions like
    ///                                        `(a + b * 2)` for CPT
    ///     branch  := '[' items (',' items)* ']'
    ///
    /// We tokenize loosely: an instruction's args are taken as the literal text
    /// inside its parens (stripped of leading/trailing whitespace, comma-split
    /// at top level). CPT-style expressions are passed to the evaluator as
    /// strings, which it then parses with a small numeric expression engine.
    /// </summary>
    public abstract class RungItem { }

    public class Instr : RungItem
    {
        public string Name = "";
        public List<string> Args = new();
        public override string ToString() => $"{Name}({string.Join(",", Args)})";
    }

    public class Branch : RungItem
    {
        public List<List<RungItem>> Paths = new();
    }

    public static class RungParser
    {
        public static List<RungItem> Parse(string rungText)
        {
            if (string.IsNullOrWhiteSpace(rungText)) return new List<RungItem>();
            var text = rungText.Trim();
            if (text.EndsWith(";")) text = text.Substring(0, text.Length - 1);
            var pos = 0;
            return ParseItems(text, ref pos, terminators: "");
        }

        private static List<RungItem> ParseItems(string s, ref int pos, string terminators)
        {
            var items = new List<RungItem>();
            while (pos < s.Length)
            {
                char c = s[pos];
                if (terminators.IndexOf(c) >= 0) break;
                if (char.IsWhiteSpace(c)) { pos++; continue; }
                if (c == '[')
                {
                    pos++; // skip [
                    var branch = new Branch();
                    while (true)
                    {
                        var path = ParseItems(s, ref pos, ",]");
                        branch.Paths.Add(path);
                        if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                        break;
                    }
                    if (pos < s.Length && s[pos] == ']') pos++;
                    items.Add(branch);
                    continue;
                }
                // identifier-start: instruction
                if (char.IsLetter(c) || c == '_')
                {
                    items.Add(ParseInstr(s, ref pos));
                    continue;
                }
                // unknown char — skip
                pos++;
            }
            return items;
        }

        private static Instr ParseInstr(string s, ref int pos)
        {
            var instr = new Instr();
            var nameStart = pos;
            while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_')) pos++;
            instr.Name = s.Substring(nameStart, pos - nameStart).ToUpperInvariant();

            // optional args
            if (pos < s.Length && s[pos] == '(')
            {
                pos++; // skip (
                int depth = 1;
                var sb = new StringBuilder();
                while (pos < s.Length && depth > 0)
                {
                    char ch = s[pos];
                    if (ch == '(') { depth++; sb.Append(ch); pos++; continue; }
                    if (ch == ')')
                    {
                        depth--;
                        if (depth == 0) { pos++; break; }
                        sb.Append(ch); pos++; continue;
                    }
                    if (ch == '[')
                    {
                        // indexer inside an arg — copy verbatim including nested brackets
                        int bd = 1;
                        sb.Append(ch); pos++;
                        while (pos < s.Length && bd > 0)
                        {
                            char bc = s[pos];
                            if (bc == '[') bd++;
                            else if (bc == ']') bd--;
                            if (bd > 0) sb.Append(bc);
                            else sb.Append(bc);
                            pos++;
                        }
                        continue;
                    }
                    if (ch == ',' && depth == 1)
                    {
                        instr.Args.Add(sb.ToString().Trim());
                        sb.Clear();
                        pos++; continue;
                    }
                    sb.Append(ch);
                    pos++;
                }
                if (sb.Length > 0) instr.Args.Add(sb.ToString().Trim());
            }
            return instr;
        }
    }

    /// <summary>
    /// Tiny expression evaluator for CPT-style numeric expressions.
    /// Supports: + - * / MOD AND OR XOR &lt;&lt; &gt;&gt; unary - NOT()
    ///           tag refs, member access (t.ACC), array indexing (a[i]),
    ///           integer + float literals.
    /// </summary>
    public class ExpressionEvaluator
    {
        private readonly TagDatabase _db;
        private string _text = "";
        private int _pos;

        public ExpressionEvaluator(TagDatabase db) { _db = db; }

        public double Eval(string text)
        {
            _text = text ?? "";
            _pos = 0;
            return ParseOr();
        }

        private void Skip() { while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++; }
        private bool Peek(string s)
        {
            Skip();
            if (_pos + s.Length > _text.Length) return false;
            return string.Compare(_text, _pos, s, 0, s.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }
        private bool Take(string s)
        {
            if (!Peek(s)) return false;
            // ensure word boundary for word ops
            if (char.IsLetter(s[0]))
            {
                int after = _pos + s.Length;
                if (after < _text.Length && (char.IsLetterOrDigit(_text[after]) || _text[after] == '_'))
                    return false;
            }
            _pos += s.Length;
            return true;
        }

        private double ParseOr()
        {
            var v = ParseXor();
            while (Take("OR")) v = (long)v | (long)ParseXor();
            return v;
        }
        private double ParseXor()
        {
            var v = ParseAnd();
            while (Take("XOR")) v = (long)v ^ (long)ParseAnd();
            return v;
        }
        private double ParseAnd()
        {
            var v = ParseShift();
            while (Take("AND")) v = (long)v & (long)ParseShift();
            return v;
        }
        private double ParseShift()
        {
            var v = ParseAdd();
            while (true)
            {
                if (Take("<<")) v = ((long)v) << (int)ParseAdd();
                else if (Take(">>")) v = ((long)v) >> (int)ParseAdd();
                else break;
            }
            return v;
        }
        private double ParseAdd()
        {
            var v = ParseMul();
            while (true)
            {
                Skip();
                if (Peek("+")) { _pos++; v += ParseMul(); }
                else if (Peek("-")) { _pos++; v -= ParseMul(); }
                else break;
            }
            return v;
        }
        private double ParseMul()
        {
            var v = ParseUnary();
            while (true)
            {
                Skip();
                if (Peek("*")) { _pos++; v *= ParseUnary(); }
                else if (Peek("/")) { _pos++; var r = ParseUnary(); v = r != 0 ? v / r : 0; }
                else if (Take("MOD")) { var r = ParseUnary(); v = r != 0 ? (long)v % (long)r : 0; }
                else break;
            }
            return v;
        }
        private double ParseUnary()
        {
            Skip();
            if (Peek("-")) { _pos++; return -ParseUnary(); }
            if (Take("NOT"))
            {
                if (Peek("(")) { _pos++; var v = ParseOr(); if (Peek(")")) _pos++; return ~(long)v; }
                return ~(long)ParseUnary();
            }
            return ParsePrimary();
        }
        private double ParsePrimary()
        {
            Skip();
            if (_pos >= _text.Length) return 0;
            char c = _text[_pos];
            if (c == '(')
            {
                _pos++;
                var v = ParseOr();
                Skip();
                if (_pos < _text.Length && _text[_pos] == ')') _pos++;
                return v;
            }
            if (char.IsDigit(c) || (c == '.' && _pos + 1 < _text.Length && char.IsDigit(_text[_pos + 1])))
            {
                int start = _pos;
                while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] == '.' ||
                       _text[_pos] == 'e' || _text[_pos] == 'E' ||
                       (_pos > start && (_text[_pos] == '+' || _text[_pos] == '-') &&
                        (_text[_pos - 1] == 'e' || _text[_pos - 1] == 'E'))))
                    _pos++;
                double.TryParse(_text.Substring(start, _pos - start),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var lit);
                return lit;
            }
            // identifier (possibly with .member or [index])
            if (char.IsLetter(c) || c == '_')
            {
                int start = _pos;
                while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_')) _pos++;
                var sb = new StringBuilder(_text.Substring(start, _pos - start));
                // optional .member
                while (_pos < _text.Length && _text[_pos] == '.')
                {
                    sb.Append('.'); _pos++;
                    int mstart = _pos;
                    while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_')) _pos++;
                    sb.Append(_text.Substring(mstart, _pos - mstart));
                }
                // optional [index]
                if (_pos < _text.Length && _text[_pos] == '[')
                {
                    int depth = 1; _pos++;
                    sb.Append('[');
                    while (_pos < _text.Length && depth > 0)
                    {
                        if (_text[_pos] == '[') depth++;
                        else if (_text[_pos] == ']') depth--;
                        if (depth > 0) sb.Append(_text[_pos]);
                        else sb.Append(']');
                        _pos++;
                    }
                }
                return _db.ReadOperand(sb.ToString());
            }
            _pos++;
            return 0;
        }
    }
}
