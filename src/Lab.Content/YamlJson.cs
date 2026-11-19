using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Lab.Content;

/// <summary>Converte YAML em <see cref="JsonNode"/>. Escalares sem aspas viram número/booleano/nulo quando possível.</summary>
public static class YamlJson
{
    public static JsonNode? Parse(string yaml, string sourceName = "")
    {
        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new ContentException($"{sourceName}: YAML inválido na linha {ex.Start.Line}: {ex.Message}", ex);
        }

        return stream.Documents.Count == 0 ? null : Convert(stream.Documents[0].RootNode);
    }

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode mapping => new JsonObject(mapping.Children.Select(p =>
            KeyValuePair.Create(((YamlScalarNode)p.Key).Value ?? "", Convert(p.Value)))),
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(Convert).ToArray()),
        YamlScalarNode scalar => Scalar(scalar),
        _ => null,
    };

    private static JsonNode? Scalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? "";
        if (scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted or ScalarStyle.Literal or ScalarStyle.Folded)
            return JsonValue.Create(value);

        switch (value)
        {
            case "" or "~" or "null":
                return null;
            case "true":
                return JsonValue.Create(true);
            case "false":
                return JsonValue.Create(false);
        }

        if (long.TryParse(value.Replace("_", "", StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            return JsonValue.Create(integer);
        if (double.TryParse(value.Replace("_", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return JsonValue.Create(number);
        return JsonValue.Create(value);
    }
}

public sealed class ContentException(string message, Exception? inner = null) : Exception(message, inner);
