using System.Globalization;

namespace Lab.Ir.Templating;

/// <summary>
/// Avaliador de expressões aritméticas das sementes: números, variáveis, + - * / %, parênteses e
/// as funções ceil, floor, round, min, max, abs.
/// </summary>
public static class Expression
{
    public static double Evaluate(string text, IReadOnlyDictionary<string, double> variables)
    {
        var parser = new Parser(text, variables);
        var value = parser.ParseExpression();
        parser.ExpectEnd();
        return value;
    }

    private sealed class Parser(string text, IReadOnlyDictionary<string, double> variables)
    {
        private int _pos;

        public double ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                SkipSpaces();
                if (Match('+')) value += ParseTerm();
                else if (Match('-')) value -= ParseTerm();
                else return value;
            }
        }

        private double ParseTerm()
        {
            var value = ParseUnary();
            while (true)
            {
                SkipSpaces();
                if (Match('*')) value *= ParseUnary();
                else if (Match('/'))
                {
                    var divisor = ParseUnary();
                    value = divisor == 0 ? throw Error("divisão por zero") : value / divisor;
                }
                else if (Match('%')) value %= ParseUnary();
                else return value;
            }
        }

        private double ParseUnary()
        {
            SkipSpaces();
            if (Match('-')) return -ParseUnary();
            if (Match('+')) return ParseUnary();
            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            SkipSpaces();
            if (Match('('))
            {
                var inner = ParseExpression();
                SkipSpaces();
                if (!Match(')')) throw Error("esperado ')'");
                return inner;
            }

            if (_pos < text.Length && (char.IsDigit(text[_pos]) || text[_pos] == '.'))
            {
                var start = _pos;
                while (_pos < text.Length && (char.IsDigit(text[_pos]) || text[_pos] is '.' or '_'))
                    _pos++;
                var literal = text[start.._pos].Replace("_", "", StringComparison.Ordinal);
                return double.Parse(literal, CultureInfo.InvariantCulture);
            }

            if (_pos < text.Length && (char.IsLetter(text[_pos]) || text[_pos] == '_'))
            {
                var start = _pos;
                while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] == '_'))
                    _pos++;
                var name = text[start.._pos];
                SkipSpaces();
                if (Match('('))
                {
                    var args = new List<double>();
                    SkipSpaces();
                    if (!Match(')'))
                    {
                        do
                        {
                            args.Add(ParseExpression());
                            SkipSpaces();
                        }
                        while (Match(','));
                        if (!Match(')')) throw Error("esperado ')' após os argumentos");
                    }

                    return Call(name, args);
                }

                return variables.TryGetValue(name, out var value) ? value : throw Error($"variável desconhecida '{name}'");
            }

            throw Error("expressão inválida");
        }

        private double Call(string name, List<double> args) => name switch
        {
            "ceil" when args.Count == 1 => Math.Ceiling(args[0]),
            "floor" when args.Count == 1 => Math.Floor(args[0]),
            "round" when args.Count == 1 => Math.Round(args[0], MidpointRounding.AwayFromZero),
            "abs" when args.Count == 1 => Math.Abs(args[0]),
            "min" when args.Count >= 1 => args.Min(),
            "max" when args.Count >= 1 => args.Max(),
            _ => throw Error($"função desconhecida '{name}' com {args.Count} argumento(s)"),
        };

        public void ExpectEnd()
        {
            SkipSpaces();
            if (_pos != text.Length) throw Error("texto sobrando");
        }

        private bool Match(char c)
        {
            if (_pos < text.Length && text[_pos] == c)
            {
                _pos++;
                return true;
            }

            return false;
        }

        private void SkipSpaces()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++;
        }

        private FormatException Error(string message) => new($"Expressão '{text}': {message} (posição {_pos}).");
    }
}
