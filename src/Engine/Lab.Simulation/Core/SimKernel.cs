namespace Lab.Simulation.Core;

/// <summary>Núcleo de eventos discretos: relógio virtual em microssegundos e fila de prioridade com desempate por sequência.</summary>
public sealed class SimKernel
{
    private readonly PriorityQueue<Action, (long Time, long Seq)> _queue = new();
    private long _sequence;

    public long Now { get; private set; }

    public long Events { get; private set; }

    public long EventBudget { get; init; } = long.MaxValue;

    public bool BudgetExceeded { get; private set; }

    public int Pending => _queue.Count;

    public double NowSeconds => Now / 1_000_000.0;

    public void At(long timeUs, Action action) => _queue.Enqueue(action, (Math.Max(timeUs, Now), _sequence++));

    public void AfterUs(long delayUs, Action action) => At(Now + Math.Max(0, delayUs), action);

    public void AfterMs(double delayMs, Action action) => AfterUs((long)Math.Round(Math.Max(0, delayMs) * 1000), action);

    public void RunUntil(long endUs, CancellationToken cancellationToken = default)
    {
        while (_queue.TryPeek(out _, out var key) && key.Time <= endUs)
        {
            if (Events >= EventBudget)
            {
                BudgetExceeded = true;
                return;
            }

            var action = _queue.Dequeue();
            Now = key.Time;
            Events++;
            if ((Events & 0x3FFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            action();
        }

        if (Now < endUs) Now = endUs;
    }
}
