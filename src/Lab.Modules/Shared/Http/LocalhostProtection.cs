using Microsoft.AspNetCore.Http;

namespace Lab.Modules.Shared.Http;

/// <summary>
/// Proteções do app local (spec 09): requisições que alteram estado precisam do header <c>X-Lab-Client</c> — que outro
/// site não consegue enviar sem preflight de CORS, e o app não habilita CORS — e de um <c>Origin</c> conhecido.
/// A validação do <c>Host</c> contra DNS rebinding fica no <c>AllowedHosts</c> do ASP.NET Core.
/// </summary>
public sealed class LocalhostProtectionMiddleware(RequestDelegate next, IReadOnlyCollection<string> allowedOrigins)
{
    public const string ClientHeader = "X-Lab-Client";

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsMutation(context.Request.Method) && context.Request.Path.StartsWithSegments("/api"))
        {
            if (!context.Request.Headers.ContainsKey(ClientHeader))
            {
                await Reject(context, $"Requisições que alteram dados precisam do header {ClientHeader}.");
                return;
            }

            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && !IsAllowedOrigin(context, origin))
            {
                await Reject(context, $"Origem não permitida: {origin}.");
                return;
            }
        }

        await next(context);
    }

    private bool IsAllowedOrigin(HttpContext context, string origin)
    {
        var self = $"{context.Request.Scheme}://{context.Request.Host}";
        return string.Equals(origin, self, StringComparison.OrdinalIgnoreCase)
            || allowedOrigins.Any(o => string.Equals(o.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsMutation(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    private static Task Reject(HttpContext context, string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status403Forbidden, title: "Requisição recusada").ExecuteAsync(context);
}
