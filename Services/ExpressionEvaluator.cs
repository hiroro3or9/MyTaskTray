using System.Globalization;

namespace MyTaskTray.Services
{
    /// <summary>式を解釈できなかったことを表す。</summary>
    public sealed class ExpressionException(string message) : Exception(message)
    {
    }

    /// <summary>
    /// <c>{calc:...}</c> の中身を評価する小さな数式エンジン。
    ///
    /// 対応する記法:
    ///   数値       1  1.5  1_000_000（_ は桁区切りとして無視。カンマは引数の区切り）
    ///   演算子     + - * / ^（べき乗） 単項の -
    ///              単項の - はべき乗より強く結び付くため、-2^2 は 4 になる（Excel と同じ）。
    ///              -(2^2) を意図する場合はかっこで書く。
    ///   後置 %     8% → 0.08（1000*8% で消費税額）
    ///   かっこ     ( )
    ///   定数       pi  e
    ///   関数       round(x[,桁]) floor(x[,桁]) ceil(x[,桁]) trunc(x[,桁])
    ///              abs(x) sign(x) sqrt(x) pow(x,y) mod(a,b)
    ///              min(...) max(...) sum(...) avg(...) log(x[,底]) log10(x) exp(x)
    ///
    /// 計算は <see cref="decimal"/> で行うため、金額の計算でも誤差が出にくい。
    /// sqrt / log / exp と非整数のべき乗のみ double を経由する。
    /// </summary>
    public static class ExpressionEvaluator
    {
        private const int MaxDecimals = 28;

        /// <summary>
        /// 書式を指定しなかったときの表記。<c>#</c> は末尾の 0 を出さないため
        /// 2 は "2"、1.5 は "1.5" になる。1/3 のような割り切れない値は小数 10 桁で丸める。
        /// </summary>
        private const string DefaultFormat = "0.##########";

        /// <summary>
        /// 式を評価する。解釈できない場合・計算できない場合は必ず
        /// <see cref="ExpressionException"/> を投げる。
        /// </summary>
        public static decimal Evaluate(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                throw new ExpressionException("式が空です。");
            }

            try
            {
                Parser parser = new(expression);
                decimal value = parser.ParseExpression();
                parser.ExpectEnd();
                return value;
            }
            catch (OverflowException)
            {
                // decimal の範囲を超えた場合。呼び出し側が例外の種類を気にしなくて済むよう包み直す
                throw new ExpressionException("計算結果が大きすぎます。");
            }
            catch (DivideByZeroException)
            {
                throw new ExpressionException("0 で割ることはできません。");
            }
        }

        /// <summary>
        /// 評価結果を文字列にする。書式が空なら小数部が不要なら整数として、
        /// 必要なら末尾の 0 を落とした表記で返す。
        /// </summary>
        public static string Format(decimal value, string format)
            => string.IsNullOrEmpty(format)
                ? value.ToString(DefaultFormat, CultureInfo.InvariantCulture)
                : value.ToString(format, CultureInfo.CurrentCulture);

        /// <summary>再帰下降パーサ。字句解析は必要な位置で直接読み進める。</summary>
        private sealed class Parser(string text)
        {
            private int _pos = 0;

            public void ExpectEnd()
            {
                SkipSpace();
                if (_pos < text.Length)
                {
                    throw new ExpressionException($"'{text[_pos]}' 以降を解釈できません。");
                }
            }

            /// <summary>加減算。</summary>
            public decimal ParseExpression()
            {
                decimal left = ParseTerm();

                while (true)
                {
                    SkipSpace();
                    char op = Peek();
                    if (op != '+' && op != '-')
                    {
                        return left;
                    }

                    _pos++;
                    decimal right = ParseTerm();
                    left = op == '+' ? left + right : left - right;
                }
            }

            /// <summary>乗除算。</summary>
            private decimal ParseTerm()
            {
                decimal left = ParsePower();

                while (true)
                {
                    SkipSpace();
                    char op = Peek();
                    if (op != '*' && op != '/' && op != '×' && op != '÷')
                    {
                        return left;
                    }

                    _pos++;
                    decimal right = ParsePower();

                    if (op == '*' || op == '×')
                    {
                        left *= right;
                    }
                    else
                    {
                        if (right == 0m)
                        {
                            throw new ExpressionException("0 で割ることはできません。");
                        }

                        left /= right;
                    }
                }
            }

            /// <summary>べき乗（右結合）。</summary>
            private decimal ParsePower()
            {
                decimal value = ParseUnary();

                SkipSpace();
                if (Peek() == '^')
                {
                    _pos++;
                    decimal exponent = ParsePower();
                    return Power(value, exponent);
                }

                return value;
            }

            private decimal ParseUnary()
            {
                SkipSpace();
                char c = Peek();

                if (c == '-')
                {
                    _pos++;
                    return -ParseUnary();
                }

                if (c == '+')
                {
                    _pos++;
                    return ParseUnary();
                }

                return ParsePostfix();
            }

            /// <summary>後置の % を適用する（8% → 0.08）。</summary>
            private decimal ParsePostfix()
            {
                decimal value = ParsePrimary();

                while (true)
                {
                    SkipSpace();
                    if (Peek() != '%')
                    {
                        return value;
                    }

                    _pos++;
                    value /= 100m;
                }
            }

            private decimal ParsePrimary()
            {
                SkipSpace();

                if (_pos >= text.Length)
                {
                    throw new ExpressionException("式が途中で終わっています。");
                }

                char c = text[_pos];

                if (c == '(')
                {
                    _pos++;
                    decimal value = ParseExpression();
                    SkipSpace();
                    if (Peek() != ')')
                    {
                        throw new ExpressionException("かっこが閉じられていません。");
                    }

                    _pos++;
                    return value;
                }

                if (char.IsAsciiDigit(c) || c == '.')
                {
                    return ParseNumber();
                }

                if (char.IsAsciiLetter(c) || c == '_')
                {
                    return ParseIdentifier();
                }

                throw new ExpressionException($"'{c}' は使えません。");
            }

            private decimal ParseNumber()
            {
                int start = _pos;
                bool hasDot = false;

                while (_pos < text.Length)
                {
                    char c = text[_pos];

                    // カンマは関数の引数の区切りなので、桁区切りには _ を使う（1_000_000）
                    if (char.IsAsciiDigit(c) || c == '_')
                    {
                        _pos++;
                        continue;
                    }

                    if (c == '.' && !hasDot)
                    {
                        hasDot = true;
                        _pos++;
                        continue;
                    }

                    break;
                }

                string raw = text[start.._pos].Replace("_", string.Empty);

                if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value))
                {
                    throw new ExpressionException($"数値 '{raw}' を解釈できません。");
                }

                return value;
            }

            private decimal ParseIdentifier()
            {
                int start = _pos;
                while (_pos < text.Length && (char.IsAsciiLetterOrDigit(text[_pos]) || text[_pos] == '_'))
                {
                    _pos++;
                }

                string name = text[start.._pos].ToLowerInvariant();

                SkipSpace();
                if (Peek() != '(')
                {
                    return name switch
                    {
                        "pi" => 3.1415926535897932384626433833m,
                        "e" => 2.7182818284590452353602874714m,
                        _ => throw new ExpressionException($"'{name}' が何かわかりません。"),
                    };
                }

                _pos++;
                List<decimal> args = [];

                SkipSpace();
                if (Peek() == ')')
                {
                    _pos++;
                }
                else
                {
                    while (true)
                    {
                        args.Add(ParseExpression());
                        SkipSpace();
                        char c = Peek();

                        if (c == ',')
                        {
                            _pos++;
                            continue;
                        }

                        if (c == ')')
                        {
                            _pos++;
                            break;
                        }

                        throw new ExpressionException($"{name}() の引数を解釈できません。");
                    }
                }

                return Call(name, args);
            }

            private char Peek() => _pos < text.Length ? text[_pos] : '\0';

            private void SkipSpace()
            {
                while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
                {
                    _pos++;
                }
            }
        }

        private static decimal Call(string name, List<decimal> args)
        {
            switch (name)
            {
                case "round":
                    return Math.Round(Arg(name, args, 0), Digits(name, args), MidpointRounding.AwayFromZero);

                case "floor":
                    return Scaled(name, args, static (v, s) => Math.Floor(v * s) / s);

                case "ceil":
                case "ceiling":
                    return Scaled(name, args, static (v, s) => Math.Ceiling(v * s) / s);

                case "trunc":
                case "int":
                    return Scaled(name, args, static (v, s) => Math.Truncate(v * s) / s);

                case "abs":
                    return Math.Abs(One(name, args));

                case "sign":
                    return Math.Sign(One(name, args));

                case "sqrt":
                    decimal target = One(name, args);
                    if (target < 0m)
                    {
                        throw new ExpressionException("sqrt() に負の数は使えません。");
                    }

                    return FromDouble(Math.Sqrt((double)target));

                case "pow":
                    Require(name, args, 2);
                    return Power(args[0], args[1]);

                case "mod":
                    Require(name, args, 2);
                    if (args[1] == 0m)
                    {
                        throw new ExpressionException("mod() の 2 番目に 0 は使えません。");
                    }

                    return args[0] % args[1];

                case "min":
                    RequireAtLeast(name, args, 1);
                    return args.Min();

                case "max":
                    RequireAtLeast(name, args, 1);
                    return args.Max();

                case "sum":
                    RequireAtLeast(name, args, 1);
                    return args.Sum();

                case "avg":
                case "average":
                    RequireAtLeast(name, args, 1);
                    return args.Sum() / args.Count;

                case "log":
                    RequireAtLeast(name, args, 1);
                    if (args.Count > 2)
                    {
                        throw new ExpressionException("log() の引数が多すぎます。");
                    }

                    Positive(name, args[0]);
                    return args.Count == 1
                        ? FromDouble(Math.Log((double)args[0]))
                        : FromDouble(Math.Log((double)args[0], (double)args[1]));

                case "log10":
                    Positive(name, One(name, args));
                    return FromDouble(Math.Log10((double)args[0]));

                case "exp":
                    return FromDouble(Math.Exp((double)One(name, args)));

                default:
                    throw new ExpressionException($"{name}() という関数はありません。");
            }
        }

        private static decimal Power(decimal value, decimal exponent)
        {
            // 整数のべき乗は decimal のまま計算し、金額計算での誤差を避ける
            if (exponent == Math.Truncate(exponent) && Math.Abs(exponent) <= 28m)
            {
                int times = (int)Math.Abs(exponent);
                decimal result = 1m;
                for (int i = 0; i < times; i++)
                {
                    result *= value;
                }

                if (exponent >= 0m)
                {
                    return result;
                }

                if (result == 0m)
                {
                    throw new ExpressionException("0 で割ることはできません。");
                }

                return 1m / result;
            }

            return FromDouble(Math.Pow((double)value, (double)exponent));
        }

        private static decimal FromDouble(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ExpressionException("計算結果が数値になりません。");
            }

            return (decimal)value;
        }

        private static decimal One(string name, List<decimal> args)
        {
            Require(name, args, 1);
            return args[0];
        }

        private static decimal Arg(string name, List<decimal> args, int index)
        {
            RequireAtLeast(name, args, index + 1);
            return args[index];
        }

        /// <summary>桁数指定つきの丸め系関数の共通処理。</summary>
        private static decimal Scaled(string name, List<decimal> args, Func<decimal, decimal, decimal> apply)
        {
            decimal value = Arg(name, args, 0);
            int digits = Digits(name, args);
            decimal scale = 1m;
            for (int i = 0; i < digits; i++)
            {
                scale *= 10m;
            }

            return apply(value, scale);
        }

        private static int Digits(string name, List<decimal> args)
        {
            if (args.Count < 2)
            {
                return 0;
            }

            if (args.Count > 2)
            {
                throw new ExpressionException($"{name}() の引数が多すぎます。");
            }

            decimal digits = args[1];
            if (digits != Math.Truncate(digits) || digits < 0m || digits > MaxDecimals)
            {
                throw new ExpressionException($"{name}() の桁数は 0〜{MaxDecimals} の整数で指定してください。");
            }

            return (int)digits;
        }

        private static void Positive(string name, decimal value)
        {
            if (value <= 0m)
            {
                throw new ExpressionException($"{name}() には正の数を指定してください。");
            }
        }

        private static void Require(string name, List<decimal> args, int count)
        {
            if (args.Count != count)
            {
                throw new ExpressionException($"{name}() の引数は {count} 個です。");
            }
        }

        private static void RequireAtLeast(string name, List<decimal> args, int count)
        {
            if (args.Count < count)
            {
                throw new ExpressionException($"{name}() には引数が {count} 個以上必要です。");
            }
        }
    }
}
