using System.Threading.Channels;
using Lab.Modules.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lab.Modules.Shared.Jobs;

/// <summary>Executa um tipo de job. Precisa ser idempotente: o job é entregue pelo menos uma vez.</summary>
public interface IJobHandler
{
    string Type { get; }

    Task HandleAsync(Guid jobId, string payloadJson, CancellationToken cancellationToken);

    /// <summary>Chamado quando o job esgota as tentativas (ex.: marcar a avaliação como falha técnica).</summary>
    Task OnFailedAsync(string payloadJson, string error, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Acorda o worker quando um job novo é gravado (o worker também varre a tabela periodicamente).</summary>
public sealed class JobSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }
}

public static class JobScheduler
{
    /// <summary>Grava o job no contexto (o chamador salva na mesma transação da mudança e depois chama <see cref="JobSignal.Notify"/>).</summary>
    public static JobEntity Add(LabDbContext db, TimeProvider clock, string type, object payload)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var job = new JobEntity { Id = Guid.CreateVersion7(), Type = type, PayloadJson = Json.Serialize(payload), RunAfter = now, CreatedAt = now };
        db.Jobs.Add(job);
        return job;
    }
}

public sealed class JobWorkerOptions
{
    public int MaxAttempts { get; set; } = 3;
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Worker em background: pega um job por vez (a simulação já usa os núcleos), com lease para retomar jobs de um
/// processo que caiu, e backoff exponencial entre tentativas.
/// </summary>
public sealed class JobWorker(IServiceScopeFactory scopes, JobSignal signal, TimeProvider clock, JobWorkerOptions options, ILogger<JobWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            JobEntity? job;
            try
            {
                job = await ClaimNextAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falha ao buscar o próximo job.");
                job = null;
            }

            if (job is null)
            {
                await signal.WaitAsync(options.PollInterval, stoppingToken);
                continue;
            }

            await RunAsync(job, stoppingToken);
        }
    }

    private async Task<JobEntity?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LabDbContext>();
        var now = clock.GetUtcNow().UtcDateTime;
        var job = await db.Jobs
            .Where(j => (j.Status == JobStatus.Queued && j.RunAfter <= now) || (j.Status == JobStatus.Running && j.LockedUntil < now))
            .OrderBy(j => j.RunAfter)
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null) return null;

        job.Status = JobStatus.Running;
        job.Attempts++;
        job.LockedUntil = now + options.Lease;
        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    private async Task RunAsync(JobEntity job, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var handler = scope.ServiceProvider.GetServices<IJobHandler>().FirstOrDefault(h => h.Type == job.Type);
        string? error = null;
        if (handler is null) error = $"Nenhum handler para o job '{job.Type}'.";
        else
        {
            try
            {
                await handler.HandleAsync(job.Id, job.PayloadJson, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return; // desligando: o lease expira e outro processo (ou o próximo início) retoma
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job {JobId} ({Type}) falhou na tentativa {Attempt}.", job.Id, job.Type, job.Attempts);
                error = ex.Message;
            }
        }

        var db = scope.ServiceProvider.GetRequiredService<LabDbContext>();
        var stored = await db.Jobs.FirstAsync(j => j.Id == job.Id, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        if (error is null)
        {
            stored.Status = JobStatus.Done;
            stored.CompletedAt = now;
            stored.LastError = null;
        }
        else if (stored.Attempts >= options.MaxAttempts || handler is null)
        {
            stored.Status = JobStatus.Failed;
            stored.CompletedAt = now;
            stored.LastError = error;
            if (handler is not null) await handler.OnFailedAsync(stored.PayloadJson, error, cancellationToken);
        }
        else
        {
            stored.Status = JobStatus.Queued;
            stored.LastError = error;
            stored.RunAfter = now + TimeSpan.FromSeconds(Math.Pow(2, stored.Attempts));
        }

        stored.LockedUntil = null;
        await db.SaveChangesAsync(cancellationToken);
    }
}
