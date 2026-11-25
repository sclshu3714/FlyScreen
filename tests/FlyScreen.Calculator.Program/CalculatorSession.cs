using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FlyScreen.ProgramDevHost
{
    /// <summary>
    /// Expression calculator. Kept in C# 5 so Windows PowerShell 5.1 Add-Type can compile it.
    /// The loose-XAML host has no code-behind; it loads this file and forwards button tags.
    /// </summary>
    public sealed class CalculatorSession
    {
        const string SqrtChar = "\u221A";
        const string SquareChar = "\u00B2";
        const string MinusChar = "\u2212";
        const string ErrorText = "\u9519\u8BEF";

        enum TKind
        {
            Num, Add, Sub, Mul, Div, UMinus, Sqrt, Sqr, Pct, LParen, RParen
        }

        sealed class Tok
        {
            public TKind Kind;
            public double Num;
        }

        string _expr = "";
        string _committed = "";
        bool _justEvaluated;
        bool _error;

        public CalculatorSession()
        {
            ExpressionText = "";
            ResultText = "0";
        }

        public string ExpressionText { get; private set; }
        public string ResultText { get; private set; }

        public void Input(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return;

            if (tag == "C")
            {
                Clear();
                Publish();
                return;
            }

            if (_error)
            {
                Clear();
                if (tag == "BS")
                {
                    Publish();
                    return;
                }
            }

            if (tag.Length == 1 && tag[0] >= '0' && tag[0] <= '9') AppendDigit(tag[0]);
            else if (tag == ".") AppendDot();
            else if (tag == "+" || tag == "-" || tag == "*" || tag == "/") AppendOp(tag[0]);
            else if (tag == "(") AppendLeft();
            else if (tag == ")") AppendRight();
            else if (tag == "=") EvaluateFinal();
            else if (tag == "BS") Backspace();
            else if (tag == "NEG") ToggleSign();
            else if (tag == "%") Postfix("%");
            else if (tag == "SQR") Postfix(SquareChar);
            else if (tag == "SQRT") PrefixSqrt();
            else if (tag == "INV") Reciprocal();
            else return;

            if (!_error) Publish();
        }

        public static string TagFromKey(string key, bool shift)
        {
            if (string.IsNullOrEmpty(key)) return "";

            if (key.Length == 2 && key[0] == 'D' && key[1] >= '0' && key[1] <= '9')
            {
                char d = key[1];
                if (shift)
                {
                    if (d == '5') return "%";
                    if (d == '8') return "*";
                    if (d == '9') return "(";
                    if (d == '0') return ")";
                    return "";
                }
                return d.ToString();
            }

            if (key.Length == 7 && key.StartsWith("NumPad"))
            {
                char d = key[6];
                if (d >= '0' && d <= '9') return d.ToString();
            }

            if (key == "Add") return "+";
            if (key == "Subtract" || key == "OemMinus") return "-";
            if (key == "Multiply") return "*";
            if (key == "Divide") return "/";
            if (key == "Decimal" || key == "OemPeriod") return ".";
            if (key == "OemPlus") return shift ? "+" : "=";
            if (key == "Oem2") return shift ? "" : "/";
            if (key == "Enter" || key == "Return") return "=";
            if (key == "Back" || key == "Delete") return "BS";
            if (key == "Escape") return "C";
            return "";
        }

        void Clear()
        {
            _expr = "";
            _committed = "";
            _justEvaluated = false;
            _error = false;
        }

        void StartNewValue()
        {
            if (!_justEvaluated) return;
            _justEvaluated = false;
            _committed = "";
            _expr = "";
        }

        void ContinueFromResult()
        {
            if (!_justEvaluated) return;
            _justEvaluated = false;
            _committed = "";
        }

        void AppendDigit(char d)
        {
            if (_justEvaluated)
            {
                StartNewValue();
                _expr = d == '0' ? "0" : d.ToString();
                return;
            }

            if (_expr.Length > 0)
            {
                char last = _expr[_expr.Length - 1];
                if (last == ')' || last == SquareChar[0] || last == '%')
                {
                    _expr += "*" + d;
                    return;
                }
            }

            string num = TrailingUnsignedNumber(_expr);
            if (num == "0")
            {
                _expr = _expr.Substring(0, _expr.Length - 1) + d;
                return;
            }

            _expr += d;
        }

        void AppendDot()
        {
            if (_justEvaluated)
            {
                StartNewValue();
                _expr = "0.";
                return;
            }

            if (_expr.Length > 0)
            {
                char last = _expr[_expr.Length - 1];
                if (last == ')' || last == SquareChar[0] || last == '%')
                {
                    _expr += "*0.";
                    return;
                }
            }

            string num = TrailingUnsignedNumber(_expr);
            if (num.IndexOf('.') >= 0) return;
            _expr += num.Length == 0 ? "0." : ".";
        }

        void AppendOp(char op)
        {
            ContinueFromResult();
            if (_expr.EndsWith("."))
                _expr = _expr.Substring(0, _expr.Length - 1);

            if (_expr.Length == 0)
            {
                if (op == '-') _expr = "-";
                return;
            }

            char last = _expr[_expr.Length - 1];
            if (IsValueEnd(last))
            {
                _expr += op;
                return;
            }

            if (last == '(' || last == SqrtChar[0])
            {
                if (op == '-') _expr += "-";
                return;
            }

            if (last == '+' || last == '*' || last == '/')
            {
                if (op == '-') _expr += "-";
                else _expr = _expr.Substring(0, _expr.Length - 1) + op;
                return;
            }

            if (last == '-')
            {
                if (op == '-') return;
                _expr = _expr.Substring(0, _expr.Length - 1);
                if (_expr.Length == 0) return;
                char prev = _expr[_expr.Length - 1];
                if (prev == '+' || prev == '*' || prev == '/')
                    _expr = _expr.Substring(0, _expr.Length - 1) + op;
                else if (IsValueEnd(prev))
                    _expr += op;
            }
        }

        void AppendLeft()
        {
            if (_justEvaluated)
            {
                StartNewValue();
                _expr = "(";
                return;
            }

            if (_expr.Length > 0)
            {
                char last = _expr[_expr.Length - 1];
                if (IsValueEnd(last) || last == '.')
                {
                    if (last == '.') _expr += "0";
                    _expr += "*(";
                    return;
                }
            }

            _expr += "(";
        }

        void AppendRight()
        {
            if (_justEvaluated) return;
            if (_expr.Length == 0) return;
            if (Depth(_expr) <= 0) return;
            char last = _expr[_expr.Length - 1];
            if (!IsValueEnd(last)) return;
            _expr += ")";
        }

        void Backspace()
        {
            if (_justEvaluated)
            {
                Clear();
                return;
            }
            if (_expr.Length == 0) return;
            _expr = _expr.Substring(0, _expr.Length - 1);
        }

        void PrefixSqrt()
        {
            ContinueFromResult();
            int start = IndexOfLastAtom(_expr);
            if (start < 0)
            {
                _expr += SqrtChar;
                return;
            }

            string atom = _expr.Substring(start);
            string next;
            if (IsPlainUnsignedNumber(atom)) next = SqrtChar + atom;
            else if (IsWrapped(atom)) next = SqrtChar + atom;
            else next = SqrtChar + "(" + atom + ")";
            _expr = _expr.Substring(0, start) + next;
        }

        void Postfix(string token)
        {
            ContinueFromResult();
            int start = IndexOfLastAtom(_expr);
            if (start < 0) return;
            string atom = _expr.Substring(start);
            string next = IsPlainUnsignedNumber(atom) ? atom + token : Wrap(atom) + token;
            _expr = _expr.Substring(0, start) + next;
        }

        void Reciprocal()
        {
            ContinueFromResult();
            int start = IndexOfLastAtom(_expr);
            if (start < 0) return;
            string atom = _expr.Substring(start);
            _expr = _expr.Substring(0, start) + "(1/(" + atom + "))";
        }

        void ToggleSign()
        {
            ContinueFromResult();
            int start = IndexOfLastAtom(_expr);
            if (start < 0)
            {
                if (_expr.Length == 0 || EndsWithOpenContext(_expr)) _expr += "-";
                return;
            }

            string atom = _expr.Substring(start);
            string next;
            if (atom[0] == '-' && IsUnaryMinusIndex(atom, 0))
            {
                next = atom.Substring(1);
                if (next.Length == 0) next = "0";
            }
            else if (IsPlainUnsignedNumber(atom)) next = "-" + atom;
            else next = "-(" + atom + ")";
            _expr = _expr.Substring(0, start) + next;
        }

        void EvaluateFinal()
        {
            if (_expr.Length == 0) return;
            if (EndsIncomplete(_expr))
            {
                Fail();
                return;
            }

            string expr = CloseParens(_expr);
            string formatted;
            if (!TryEvaluate(expr, out formatted))
            {
                Fail();
                return;
            }

            _committed = expr;
            _expr = formatted;
            _justEvaluated = true;
        }

        void Fail()
        {
            _error = true;
            _justEvaluated = false;
            ExpressionText = Pretty(_expr);
            ResultText = ErrorText;
        }

        void Publish()
        {
            if (_error) return;

            if (_justEvaluated)
            {
                ExpressionText = _committed.Length == 0 ? "" : Pretty(_committed) + " =";
                ResultText = DisplayNumber(_expr);
                return;
            }

            ExpressionText = Pretty(_expr);
            if (_expr.Length == 0)
            {
                ResultText = "0";
                return;
            }

            int start = IndexOfLastAtom(_expr);
            if (start >= 0)
            {
                string bare;
                if (TryBareDisplay(_expr.Substring(start), out bare))
                {
                    ResultText = bare;
                    return;
                }
            }

            string preview = CloseParens(TrimIncomplete(_expr));
            string formatted;
            if (preview.Length > 0 && TryEvaluate(preview, out formatted))
            {
                ResultText = DisplayNumber(formatted);
                return;
            }

            if (!EndsIncomplete(_expr) && preview.Length > 0)
            {
                ResultText = ErrorText;
                return;
            }

            string tail = LastPlainTail(_expr);
            ResultText = tail.Length == 0 ? "0" : DisplayNumber(tail);
        }

        static bool TryEvaluate(string expr, out string formatted)
        {
            formatted = "";
            var tokens = new List<Tok>();
            if (!TryTokenize(expr, tokens)) return false;
            var rpn = new List<Tok>();
            if (!TryToRpn(tokens, rpn)) return false;
            double value;
            if (!TryEvaluateRpn(rpn, out value)) return false;
            formatted = FormatNumber(value);
            return formatted.Length > 0;
        }

        static bool TryTokenize(string s, List<Tok> tokens)
        {
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t')
                {
                    i++;
                    continue;
                }

                if (char.IsDigit(c) || c == '.')
                {
                    double n;
                    int next;
                    if (!TryReadNumber(s, i, out n, out next)) return false;
                    var t = new Tok();
                    t.Kind = TKind.Num;
                    t.Num = n;
                    tokens.Add(t);
                    i = next;
                    continue;
                }

                var op = new Tok();
                if (c == '+') op.Kind = TKind.Add;
                else if (c == '-' && IsUnaryMinusIndex(s, i)) op.Kind = TKind.UMinus;
                else if (c == '-') op.Kind = TKind.Sub;
                else if (c == '*') op.Kind = TKind.Mul;
                else if (c == '/') op.Kind = TKind.Div;
                else if (c == SqrtChar[0]) op.Kind = TKind.Sqrt;
                else if (c == SquareChar[0]) op.Kind = TKind.Sqr;
                else if (c == '%') op.Kind = TKind.Pct;
                else if (c == '(') op.Kind = TKind.LParen;
                else if (c == ')') op.Kind = TKind.RParen;
                else return false;
                tokens.Add(op);
                i++;
            }
            return tokens.Count > 0;
        }

        static bool TryReadNumber(string s, int i, out double value, out int next)
        {
            value = 0;
            next = i;
            int j = i;
            bool dot = false;
            bool any = false;
            while (j < s.Length)
            {
                char c = s[j];
                if (char.IsDigit(c))
                {
                    any = true;
                    j++;
                }
                else if (c == '.' && !dot)
                {
                    dot = true;
                    j++;
                }
                else break;
            }
            if (!any) return false;
            string token = s.Substring(i, j - i);
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return false;
            next = j;
            return true;
        }

        static bool TryToRpn(List<Tok> tokens, List<Tok> output)
        {
            var stack = new List<Tok>();
            for (int i = 0; i < tokens.Count; i++)
            {
                Tok t = tokens[i];
                if (t.Kind == TKind.Num)
                {
                    output.Add(t);
                    continue;
                }
                if (t.Kind == TKind.LParen)
                {
                    stack.Add(t);
                    continue;
                }
                if (t.Kind == TKind.RParen)
                {
                    bool found = false;
                    while (stack.Count > 0)
                    {
                        Tok top = stack[stack.Count - 1];
                        stack.RemoveAt(stack.Count - 1);
                        if (top.Kind == TKind.LParen)
                        {
                            found = true;
                            break;
                        }
                        output.Add(top);
                    }
                    if (!found) return false;
                    continue;
                }
                if (!IsOperator(t.Kind)) return false;
                while (stack.Count > 0)
                {
                    Tok top = stack[stack.Count - 1];
                    if (!IsOperator(top.Kind)) break;
                    int pt = Precedence(top.Kind);
                    int pc = Precedence(t.Kind);
                    if (pt > pc || (pt == pc && !RightAssoc(t.Kind)))
                    {
                        output.Add(top);
                        stack.RemoveAt(stack.Count - 1);
                    }
                    else break;
                }
                stack.Add(t);
            }

            while (stack.Count > 0)
            {
                Tok top = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                if (top.Kind == TKind.LParen || top.Kind == TKind.RParen) return false;
                output.Add(top);
            }
            return true;
        }

        static bool TryEvaluateRpn(List<Tok> rpn, out double value)
        {
            value = 0;
            var stack = new List<double>();
            for (int i = 0; i < rpn.Count; i++)
            {
                Tok t = rpn[i];
                if (t.Kind == TKind.Num)
                {
                    stack.Add(t.Num);
                    continue;
                }

                if (t.Kind == TKind.UMinus || t.Kind == TKind.Sqrt || t.Kind == TKind.Sqr || t.Kind == TKind.Pct)
                {
                    if (stack.Count < 1) return false;
                    double a = Pop(stack);
                    double r;
                    if (t.Kind == TKind.UMinus) r = -a;
                    else if (t.Kind == TKind.Sqrt)
                    {
                        if (a < 0) return false;
                        r = Math.Sqrt(a);
                    }
                    else if (t.Kind == TKind.Sqr) r = a * a;
                    else r = a / 100.0;
                    if (double.IsNaN(r) || double.IsInfinity(r)) return false;
                    stack.Add(r);
                    continue;
                }

                if (stack.Count < 2) return false;
                double right = Pop(stack);
                double left = Pop(stack);
                double rr;
                if (t.Kind == TKind.Add) rr = left + right;
                else if (t.Kind == TKind.Sub) rr = left - right;
                else if (t.Kind == TKind.Mul) rr = left * right;
                else if (t.Kind == TKind.Div)
                {
                    if (right == 0) return false;
                    rr = left / right;
                }
                else return false;
                if (double.IsNaN(rr) || double.IsInfinity(rr)) return false;
                stack.Add(rr);
            }
            if (stack.Count != 1) return false;
            value = stack[0];
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        static double Pop(List<double> stack)
        {
            double v = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            return v;
        }

        static bool IsOperator(TKind k)
        {
            return k == TKind.Add || k == TKind.Sub || k == TKind.Mul || k == TKind.Div
                || k == TKind.UMinus || k == TKind.Sqrt || k == TKind.Sqr || k == TKind.Pct;
        }

        static int Precedence(TKind k)
        {
            switch (k)
            {
                case TKind.Add:
                case TKind.Sub: return 1;
                case TKind.Mul:
                case TKind.Div: return 2;
                case TKind.UMinus: return 3;
                case TKind.Sqrt: return 4;
                case TKind.Sqr:
                case TKind.Pct: return 5;
                default: return 0;
            }
        }

        static bool RightAssoc(TKind k)
        {
            return k == TKind.UMinus || k == TKind.Sqrt;
        }

        static string FormatNumber(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "";
            if (v == 0) return "0";
            double av = Math.Abs(v);
            string g;
            if (av >= 1e12 || av < 1e-9)
                g = v.ToString("G10", CultureInfo.InvariantCulture).Replace("E", "e");
            else
            {
                double r = Math.Round(v, 10, MidpointRounding.AwayFromZero);
                if (r == 0) return "0";
                g = r.ToString("0.##########", CultureInfo.InvariantCulture);
            }
            if (g == "-0") return "0";
            return g;
        }

        static string DisplayNumber(string formatted)
        {
            if (formatted.Length > 0 && formatted[0] == '-')
                return MinusChar + formatted.Substring(1);
            return formatted;
        }

        static string Pretty(string expr)
        {
            if (string.IsNullOrEmpty(expr)) return "";
            var sb = new StringBuilder(expr.Length);
            for (int i = 0; i < expr.Length; i++)
            {
                char c = expr[i];
                if (c == '*') sb.Append('\u00D7');
                else if (c == '/') sb.Append('\u00F7');
                else if (c == '-') sb.Append(MinusChar);
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static bool TryBareDisplay(string atom, out string display)
        {
            display = "";
            if (string.IsNullOrEmpty(atom)) return false;
            int i = 0;
            if (atom[0] == '-') i++;
            if (i >= atom.Length) return false;
            bool digit = false;
            bool dot = false;
            for (; i < atom.Length; i++)
            {
                char c = atom[i];
                if (c >= '0' && c <= '9') digit = true;
                else if (c == '.' && !dot) dot = true;
                else return false;
            }
            if (!digit) return false;
            display = DisplayNumber(atom);
            return true;
        }

        static int IndexOfLastAtom(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            int i = s.Length - 1;
            while (i >= 0 && (s[i] == SquareChar[0] || s[i] == '%')) i--;
            if (i < 0) return -1;

            if (s[i] == ')')
            {
                int depth = 1;
                int j = i - 1;
                for (; j >= 0 && depth > 0; j--)
                {
                    if (s[j] == ')') depth++;
                    else if (s[j] == '(') depth--;
                }
                if (depth != 0) return -1;
                return IncludePrefixes(s, j + 1);
            }

            if (char.IsDigit(s[i]) || s[i] == '.')
            {
                int j = i;
                while (j >= 0 && (char.IsDigit(s[j]) || s[j] == '.')) j--;
                int numStart = j + 1;
                bool hasDigit = false;
                for (int k = numStart; k <= i; k++)
                {
                    if (char.IsDigit(s[k])) hasDigit = true;
                }
                if (!hasDigit) return -1;
                return IncludePrefixes(s, numStart);
            }
            return -1;
        }

        static int IncludePrefixes(string s, int start)
        {
            while (start > 0)
            {
                char p = s[start - 1];
                if (p == SqrtChar[0])
                {
                    start--;
                    continue;
                }
                if (p == '-' && IsUnaryMinusIndex(s, start - 1))
                {
                    start--;
                    continue;
                }
                break;
            }
            return start;
        }

        static bool IsUnaryMinusIndex(string s, int index)
        {
            if (index < 0 || index >= s.Length || s[index] != '-') return false;
            if (index == 0) return true;
            char p = s[index - 1];
            return p == '+' || p == '-' || p == '*' || p == '/' || p == '(' || p == SqrtChar[0];
        }

        static bool IsPlainUnsignedNumber(string atom)
        {
            if (string.IsNullOrEmpty(atom)) return false;
            bool digit = false;
            bool dot = false;
            for (int i = 0; i < atom.Length; i++)
            {
                char c = atom[i];
                if (char.IsDigit(c)) digit = true;
                else if (c == '.' && !dot) dot = true;
                else return false;
            }
            return digit;
        }

        static bool IsWrapped(string atom)
        {
            return atom.Length >= 2 && atom[0] == '(' && atom[atom.Length - 1] == ')';
        }

        static string Wrap(string atom)
        {
            if (IsWrapped(atom)) return atom;
            return "(" + atom + ")";
        }

        static bool IsValueEnd(char c)
        {
            return char.IsDigit(c) || c == ')' || c == SquareChar[0] || c == '%';
        }

        static bool EndsIncomplete(string s)
        {
            if (s.Length == 0) return true;
            char c = s[s.Length - 1];
            return c == '+' || c == '-' || c == '*' || c == '/' || c == SqrtChar[0] || c == '(';
        }

        static bool EndsWithOpenContext(string s)
        {
            if (s.Length == 0) return true;
            char c = s[s.Length - 1];
            return c == '+' || c == '-' || c == '*' || c == '/' || c == SqrtChar[0] || c == '(';
        }

        static int Depth(string s)
        {
            int open = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '(') open++;
                else if (s[i] == ')') open--;
            }
            return open;
        }

        static string CloseParens(string s)
        {
            int open = Depth(s);
            if (open <= 0) return s;
            return s + new string(')', open);
        }

        static string TrimIncomplete(string s)
        {
            while (s.Length > 0 && EndsIncomplete(s))
                s = s.Substring(0, s.Length - 1);
            return s;
        }

        static string TrailingUnsignedNumber(string s)
        {
            if (s.Length == 0) return "";
            int i = s.Length - 1;
            if (!char.IsDigit(s[i]) && s[i] != '.') return "";
            int end = i;
            while (i >= 0 && (char.IsDigit(s[i]) || s[i] == '.')) i--;
            return s.Substring(i + 1, end - i);
        }

        static string LastPlainTail(string s)
        {
            int i = s.Length - 1;
            while (i >= 0)
            {
                char c = s[i];
                if (c == '+' || c == '*' || c == '/' || c == SqrtChar[0] || c == '(' || c == '-')
                {
                    i--;
                    continue;
                }
                break;
            }
            if (i < 0) return "";
            string head = s.Substring(0, i + 1);
            string num = TrailingUnsignedNumber(head);
            if (num.Length == 0) return "";
            int numStart = head.Length - num.Length;
            if (numStart > 0 && head[numStart - 1] == '-' && IsUnaryMinusIndex(head, numStart - 1))
                return "-" + num;
            return num;
        }
    }
}
