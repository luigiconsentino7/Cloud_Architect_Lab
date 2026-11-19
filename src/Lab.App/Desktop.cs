using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

/// <summary>O que só importa no executável aberto com dois cliques: abrir o navegador e lidar com a porta ocupada.</summary>
internal static class Desktop
{
    public static void OpenBrowser(string url, ILogger logger)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            logger.LogInformation("Abra {Url} no navegador.", url);
        }
    }

    /// <summary>
    /// A porta do app já está ocupada. Se for o próprio Lab (aberto antes, em outra janela), abre o navegador nele e sai;
    /// se for outro programa, explica como usar outra porta e, no executável, espera um Enter para a janela não sumir
    /// antes de a mensagem ser lida.
    /// </summary>
    public static async Task<int> PortInUseAsync(int port, bool openBrowser, bool waitForEnter, ILogger logger)
    {
        var url = $"http://localhost:{port}";
        if (await IsLabAsync(port))
        {
            logger.LogInformation("O Cloud Architect Lab já está aberto em {Url} (em outra janela); esta pode ser fechada.", url);
            if (openBrowser) OpenBrowser(url, logger);
            return 0;
        }

        logger.LogError("A porta {Port} está ocupada por outro programa. Feche esse programa ou abra o Lab em outra porta: Lab.App.exe --Lab:Port={Other}", port, port + 1);
        if (waitForEnter) WaitForEnter();
        return 1;
    }

    /// <summary>A porta está livre no loopback? Checar antes de subir evita o erro com pilha de chamadas do host.</summary>
    public static bool PortIsFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return false;
        }
    }

    /// <summary>Segura a janela do executável aberta até um Enter, para a mensagem ser lida (sem efeito sem console).</summary>
    public static void WaitForEnter()
    {
        if (Console.IsInputRedirected) return;
        Console.WriteLine("Tecle Enter para fechar.");
        Console.ReadLine();
    }

    /// <summary>Quem responde na porta é o Lab? O /api/health dele traz a versão do catálogo.</summary>
    public static async Task<bool> IsLabAsync(int port)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            var health = await http.GetFromJsonAsync<JsonElement>(new Uri($"http://127.0.0.1:{port}/api/health"));
            return health.ValueKind == JsonValueKind.Object && health.TryGetProperty("catalogVersion", out _);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return false;
        }
    }
}
