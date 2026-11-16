using Lab.Modules.Shared.Jobs;
using Lab.Modules.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lab.Modules.Shared.Outbox;

/// <summary>Consome um tipo de evento de domínio. Idempotente: a entrega é pelo menos uma vez.</summary>
public interface IOutboxHandler
{
    string EventType { get; }

    Task HandleAsync(Guid messageId, string payloadJson, CancellationToken cancellationToken);
}

public static class OutboxWriter
{
    /// <summary>Grava o evento no mesmo contexto (e na mesma transação) da mudança que o originou.</summary>
    public static void Add(LabDbContext db, TimeProvider clock, string eventType, object payload) =>
        db.Outbox.Add(new OutboxMessageEntity
        {
            Id = Guid.CreateVersion7(),
            Type = eventType,
            PayloadJson = Json.Serialize(payload),
            OccurredAt = clock.GetUtcNow().UtcDateTime,
        });
}

/// <summary>Sinal separado do dos jobs: o dispatcher acorda quando um evento é gravado.</summary>
public sealed class OutboxSignal
{
    private readonly JobSignal _inner = new();

    public void Notify() => _inner.Notify();

    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _inner.WaitAsync(timeout, cancellationToken);
}

/// <summary>Despacha os eventos do outbox em processo, em ordem, com até 10 tentativas por evento.</summary>
public sealed class OutboxDispatcher(IServiceScopeFactory scopes, OutboxSignal signal, TimeProvider clock, ILogger<OutboxDispatcher> logger)
    : BackgroundService
{
    private const int MaxAttempts = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                processed = await DispatchBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falha ao despachar o outbox.");
            }

            if (processed == 0) await signal.WaitAsync(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LabDbContext>();
        var messages = await db.Outbox
            .Where(m => m.ProcessedAt == null && m.Attempts < MaxAttempts)
            .OrderBy(m => m.OccurredAt)
            .Take(20)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            try
            {
                foreach (var handler in scope.ServiceProvider.GetServices<IOutboxHandler>().Where(h => h.EventType == message.Type))
                {
                    using var handlerScope = scopes.CreateScope();
                    var scoped = handlerScope.ServiceProvider.GetServices<IOutboxHandler>().First(h => h.GetType() == handler.GetType());
                    await scoped.HandleAsync(message.Id, message.PayloadJson, cancellationToken);
                }

                message.ProcessedAt = clock.GetUtcNow().UtcDateTime;
                message.LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.Attempts++;
                message.LastError = ex.Message;
                logger.LogError(ex, "Evento {Type} {Id} falhou (tentativa {Attempt}).", message.Type, message.Id, message.Attempts);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return messages.Count;
    }
}
