using Microsoft.AspNetCore.SignalR;

namespace Lab.Modules.Shared.Realtime;

/// <summary>Hub <c>/hubs/lab</c>: progresso da avaliação por grupo de tentativa; XP e conquistas para todos (um usuário só).</summary>
public sealed class LabHub : Hub
{
    public Task JoinAttempt(Guid attemptId) => Groups.AddToGroupAsync(Context.ConnectionId, AttemptGroup(attemptId));

    public Task LeaveAttempt(Guid attemptId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, AttemptGroup(attemptId));

    public static string AttemptGroup(Guid attemptId) => $"attempt:{attemptId:N}";
}

public sealed record EvaluationProgressEvent(Guid EvaluationId, Guid AttemptId, string Stage, double Percent, string Detail);

public sealed record EvaluationCompletedEvent(Guid EvaluationId, Guid AttemptId, string Kind, int FinalScore, string Rank);

public sealed record EvaluationFailedEvent(Guid EvaluationId, Guid AttemptId, string Error);

public sealed record XpAwardedEvent(int Amount, int TotalXp, string Reason);

public sealed record LevelUpEvent(int Level, string Title);

public sealed record AchievementUnlockedEvent(string Code, string Title);

/// <summary>Eventos de tempo real para o frontend. Falhas de envio nunca derrubam o fluxo que as gerou.</summary>
public interface ILabNotifier
{
    Task EvaluationProgress(EvaluationProgressEvent e);

    Task EvaluationCompleted(EvaluationCompletedEvent e);

    Task EvaluationFailed(EvaluationFailedEvent e);

    Task XpAwarded(XpAwardedEvent e);

    Task LevelUp(LevelUpEvent e);

    Task AchievementUnlocked(AchievementUnlockedEvent e);
}

public sealed class HubNotifier(IHubContext<LabHub> hub) : ILabNotifier
{
    public Task EvaluationProgress(EvaluationProgressEvent e) => Safe(hub.Clients.Group(LabHub.AttemptGroup(e.AttemptId)).SendAsync("EvaluationProgress", e));

    public Task EvaluationCompleted(EvaluationCompletedEvent e) => Safe(hub.Clients.All.SendAsync("EvaluationCompleted", e));

    public Task EvaluationFailed(EvaluationFailedEvent e) => Safe(hub.Clients.All.SendAsync("EvaluationFailed", e));

    public Task XpAwarded(XpAwardedEvent e) => Safe(hub.Clients.All.SendAsync("XpAwarded", e));

    public Task LevelUp(LevelUpEvent e) => Safe(hub.Clients.All.SendAsync("LevelUp", e));

    public Task AchievementUnlocked(AchievementUnlockedEvent e) => Safe(hub.Clients.All.SendAsync("AchievementUnlocked", e));

    private static async Task Safe(Task send)
    {
        try
        {
            await send;
        }
        catch (Exception)
        {
            // tempo real é melhor esforço: o frontend também consulta o estado pela API
        }
    }
}
