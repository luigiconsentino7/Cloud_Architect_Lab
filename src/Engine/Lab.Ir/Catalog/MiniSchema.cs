using System.Globalization;
using System.Text.Json.Nodes;

namespace Lab.Ir.Catalog;

/// <summary>
/// Subconjunto de JSON Schema usado nos <c>configSchema</c> do catálogo:
/// type, properties, items, enum, minimum, maximum, default. Suficiente para o inspector e sem dependências.
/// </summary>
public static class MiniSchema
{
    public static IReadOnlyList<string> Validate(JsonNode? value, JsonObject schema, string path = "")
    {
        var errors = new List<string>();
        ValidateInto(value, schema, string.IsNullOrEmpty(path) ? "config" : path, errors);
        return errors;
    }

    private static void ValidateInto(JsonNode? value, JsonObject schema, string path, List<string> errors)
    {
        if (value is null) return;
        var type = schema["type"]?.GetValue<string>();
        switch (type)
        {
            case "object":
                if (value is not JsonObject obj)
                {
                    errors.Add($"{path}: esperado um objeto.");
                    return;
                }

                if (schema["properties"] is JsonObject properties)
                {
                    foreach (var (name, child) in obj)
                    {
                        if (properties[name] is JsonObject childSchema)
                            ValidateInto(child, childSchema, $"{path}.{name}", errors);
                    }
                }

                break;
            case "array":
                if (value is not JsonArray array)
                {
                    errors.Add($"{path}: esperada uma lista.");
                    return;
                }

                if (schema["items"] is JsonObject itemSchema)
                {
                    for (var i = 0; i < array.Count; i++)
                        ValidateInto(array[i], itemSchema, $"{path}[{i}]", errors);
                }

                break;
            case "integer" or "number":
                if (value is not JsonValue numberValue || NodeConfig.ToDouble(numberValue) is not { } number)
                {
                    errors.Add($"{path}: esperado um número.");
                    return;
                }

                if (type == "integer" && Math.Abs(number - Math.Round(number)) > 1e-9)
                    errors.Add($"{path}: esperado um número inteiro.");
                if (schema["minimum"] is JsonValue min && NodeConfig.ToDouble(min) is { } minimum && number < minimum)
                    errors.Add($"{path}: mínimo {minimum.ToString(CultureInfo.InvariantCulture)}.");
                if (schema["maximum"] is JsonValue max && NodeConfig.ToDouble(max) is { } maximum && number > maximum)
                    errors.Add($"{path}: máximo {maximum.ToString(CultureInfo.InvariantCulture)}.");
                break;
            case "boolean":
                if (value is not JsonValue boolValue || !boolValue.TryGetValue<bool>(out _))
                    errors.Add($"{path}: esperado verdadeiro ou falso.");
                break;
            case "string":
                if (value is not JsonValue stringValue || !stringValue.TryGetValue<string>(out _))
                    errors.Add($"{path}: esperado um texto.");
                break;
        }

        if (schema["enum"] is JsonArray allowed && !allowed.Any(a => SameValue(a, value)))
            errors.Add($"{path}: valor fora das opções permitidas ({string.Join(", ", allowed.Select(a => a?.ToJsonString()))}).");
    }

    private static bool SameValue(JsonNode? a, JsonNode? b)
    {
        if (a is JsonValue va && b is JsonValue vb && NodeConfig.ToDouble(va) is { } da && NodeConfig.ToDouble(vb) is { } db
            && !va.TryGetValue<string>(out _) && !vb.TryGetValue<string>(out _))
            return Math.Abs(da - db) < 1e-9;
        return JsonNode.DeepEquals(a, b);
    }

    /// <summary>Devolve uma cópia da configuração com os valores <c>default</c> do schema preenchidos.</summary>
    public static JsonObject WithDefaults(JsonObject? config, JsonObject schema)
    {
        var result = config?.DeepClone() as JsonObject ?? [];
        Fill(result, schema);
        return result;
    }

    private static void Fill(JsonObject target, JsonObject schema)
    {
        if (schema["properties"] is not JsonObject properties) return;
        foreach (var (name, childNode) in properties)
        {
            if (childNode is not JsonObject childSchema) continue;
            var childType = childSchema["type"]?.GetValue<string>();
            if (target[name] is null)
            {
                if (childSchema["default"] is { } defaultValue)
                    target[name] = defaultValue.DeepClone();
                else if (childType == "object" && childSchema["properties"] is JsonObject)
                {
                    var nested = new JsonObject();
                    Fill(nested, childSchema);
                    if (nested.Count > 0) target[name] = nested;
                }
            }
            else if (childType == "object" && target[name] is JsonObject existing)
            {
                Fill(existing, childSchema);
            }
        }
    }
}
