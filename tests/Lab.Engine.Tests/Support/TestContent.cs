using Lab.Content;

namespace Lab.Engine.Tests.Support;

/// <summary>Conteúdo real do repositório (content/), carregado uma vez por execução de testes.</summary>
public static class TestContent
{
    private static readonly Lazy<ContentBundle> Bundle = new(() => ContentRepository.LoadDiscovered());

    public static ContentBundle Get() => Bundle.Value;
}
