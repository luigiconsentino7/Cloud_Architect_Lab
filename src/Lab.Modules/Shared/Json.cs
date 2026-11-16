using System.IO.Compression;
using System.Text.Json;
using Lab.Ir;

namespace Lab.Modules.Shared;

/// <summary>JSON das colunas e da API: as mesmas opções do motor (camelCase, enums em kebab-case, sem nulos).</summary>
public static class Json
{
    public static JsonSerializerOptions Options => LabJson.Options;

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidOperationException($"JSON vazio para {typeof(T).Name}.");

    public static byte[] Gzip<T>(T value)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize))
            JsonSerializer.Serialize(gzip, value, Options);
        return buffer.ToArray();
    }

    public static T Gunzip<T>(byte[] data)
    {
        using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<T>(gzip, Options) ?? throw new InvalidOperationException($"JSON vazio para {typeof(T).Name}.");
    }
}
