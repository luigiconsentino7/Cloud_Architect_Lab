namespace Lab.Cli;

/// <summary>Argumentos da linha de comando: posicionais, <c>--opção valor</c>, <c>--opção=valor</c> e flags.</summary>
public sealed class Args
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    private Args()
    {
    }

    public List<string> Positionals { get; } = [];

    /// <param name="flags">Opções sem valor (ex.: <c>quick</c>, <c>json</c>).</param>
    public static Args Parse(IEnumerable<string> args, params string[] flags)
    {
        var parsed = new Args();
        var known = new HashSet<string>(flags, StringComparer.Ordinal);
        using var e = args.GetEnumerator();
        while (e.MoveNext())
        {
            var arg = e.Current;
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                parsed.Positionals.Add(arg);
                continue;
            }

            var name = arg[2..];
            string? value = null;
            var equals = name.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }

            if (value is null && known.Contains(name))
            {
                parsed._flags.Add(name);
                continue;
            }

            if (value is null)
            {
                if (!e.MoveNext()) throw new UsageException($"--{name} precisa de um valor.");
                value = e.Current;
            }

            if (!parsed._options.TryGetValue(name, out var list))
            {
                list = [];
                parsed._options[name] = list;
            }

            list.Add(value);
        }

        return parsed;
    }

    public bool Flag(string name) => _flags.Contains(name);

    public string? Option(string name) => _options.TryGetValue(name, out var values) ? values[^1] : null;

    /// <summary>Todos os valores de uma opção repetível, aceitando também listas separadas por vírgula.</summary>
    public IReadOnlyList<string> Options(string name) =>
        _options.TryGetValue(name, out var values)
            ? values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList()
            : [];
}

public sealed class UsageException(string message) : Exception(message);
