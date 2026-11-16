using Lab.Ir;
using Lab.Ir.Catalog;
using Lab.Ir.Challenges;
using Lab.Ir.Flows;
using Lab.Simulation.Core;

namespace Lab.Simulation.Runtime.Nodes;

/// <summary>
/// Fila: entrega pelo menos uma vez, timeout de visibilidade (reentrega se o consumidor demorar), DLQ, ordem por
/// grupo (FIFO), limite de vazão e backlog quando o consumidor não acompanha.
/// </summary>
internal sealed class QueueNode : SimNode, IBroker
{
    private readonly Dictionary<string, LinkedList<Message>> _ready = new(StringComparer.Ordinal);
    private readonly HashSet<string> _subscribed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _groupsInFlight = new(StringComparer.Ordinal);
    private readonly TokenBucket _bucket;
    private readonly bool _fifo;
    private readonly double _visibilityMs;
    private readonly bool _dlq;
    private readonly int _maxReceives;
    private readonly LatencyDist _enqueue;
    private readonly LatencyDist _delivery;
    private int _inFlight;

    public QueueNode(World world, Node node) : base(world, node)
    {
        _fifo = Definition.Capability("ordering") is "per-key" or "total" || Cfg.Bool("sessions");
        var rate = Perf.MaxMessagesPerSecond ?? 100_000;
        _bucket = new TokenBucket(rate, rate);
        _visibilityMs = Math.Max(1, Cfg.Double("visibilityTimeoutSeconds", 30)) * 1000;
        _dlq = Cfg.Bool("dlq.enabled");
        _maxReceives = Math.Max(1, Cfg.Int("dlq.maxReceiveCount", 5));
        _enqueue = Perf.Latency ?? new LatencyDist(8, 40);
        _delivery = Perf.DeliveryLatency ?? new LatencyDist(15, 80);
    }

    public int Backlog => _ready.Values.Sum(l => l.Count);

    public override void Handle(Exec exec, Action<Outcome> respond) => respond(Outcome.Success);

    public void Publish(Request request, CallNode publishCall, bool countsPath, Action<Outcome> ack)
    {
        if (!base.CanAccept())
        {
            ack(Unavailable());
            return;
        }

        Arrive();
        if (!_bucket.TryTake(K.Now, ThrottleFactor))
        {
            ack(Throttle());
            return;
        }

        var consumer = publishCall.Children.FirstOrDefault(c => c.Edge!.Interaction == Interaction.AsyncConsume);
        var enqueueMs = Rng.LogNormal(_enqueue);
        if (consumer is null)
        {
            K.AfterMs(enqueueMs, () => ack(Outcome.Success));
            return;
        }

        if (countsPath) World.AsyncStarted(request, 1);
        var message = new Message { Request = request, Publish = publishCall, Consumer = consumer, EnqueuedUs = K.Now };
        Subscribe(consumer);
        K.AfterMs(enqueueMs, () =>
        {
            ack(Outcome.Success);
            K.AfterMs(Rng.LogNormal(_delivery), () => Enqueue(message, front: false));
        });
    }

    private void Subscribe(CallNode consumer)
    {
        if (_subscribed.Add(consumer.Target.Id)) World.Nodes[consumer.Target.Id].OnCapacity(Dispatch);
    }

    private void Enqueue(Message message, bool front)
    {
        var key = message.Consumer.Target.Id;
        if (!_ready.TryGetValue(key, out var list))
        {
            list = new LinkedList<Message>();
            _ready[key] = list;
        }

        message.EnqueuedUs = K.Now;
        if (front) list.AddFirst(message);
        else list.AddLast(message);
        BacklogMax = Math.Max(BacklogMax, Backlog);
        Dispatch();
    }

    private void Dispatch()
    {
        foreach (var (consumerId, list) in _ready)
        {
            var consumer = World.Nodes[consumerId];
            var cursor = list.First;
            var scanned = 0;
            while (cursor is not null && consumer.CanAccept() && scanned++ < 256)
            {
                var next = cursor.Next;
                var message = cursor.Value;
                if (_fifo && _groupsInFlight.Contains(message.Request.Key))
                {
                    cursor = next;
                    continue;
                }

                list.Remove(cursor);
                Deliver(message, consumer);
                cursor = next;
            }

            if (list.First is { } head)
                OldestAgeMaxSeconds = Math.Max(OldestAgeMaxSeconds, (K.Now - head.Value.EnqueuedUs) / 1_000_000.0);
        }
    }

    private void Deliver(Message message, SimNode consumer)
    {
        message.Deliveries++;
        var attempt = ++message.Attempt;
        _inFlight++;
        if (_fifo) _groupsInFlight.Add(message.Request.Key);
        if (message.Request.UseCase.Spec.RequiresOrdering) World.Integrity.RecordOrder(message.Request, SequenceCheck(message.Consumer));

        K.AfterMs(_visibilityMs, () =>
        {
            if (message.Acked || message.Attempt != attempt) return;
            Redeliver(message);
        });

        var exec = new Exec(message.Request, message.Consumer, message, this);
        consumer.Handle(exec, outcome =>
        {
            _inFlight--;
            if (_fifo) _groupsInFlight.Remove(message.Request.Key);
            var crashed = outcome.Ok && World.Roll(World.Probability(FaultType.ConsumerCrash));
            if (outcome.Ok && !crashed && !message.Acked)
            {
                message.Acked = true;
                if (!message.Duplicate) World.AsyncFinished(message.Request, true);
                MaybeDuplicate(message);
            }

            Dispatch();
        });
    }

    private void Redeliver(Message message)
    {
        if (message.Acked) return;
        if (_fifo) _groupsInFlight.Remove(message.Request.Key);
        if (message.Deliveries >= (_dlq ? _maxReceives : 50))
        {
            message.Acked = true;
            if (_dlq)
            {
                DlqMessages++;
                if (message.Request.Measured) World.Integrity.DlqMessages++;
            }
            else
            {
                World.LostEvent(message.Request);
            }

            if (!message.Duplicate) World.AsyncFinished(message.Request, false);
            return;
        }

        Enqueue(message, front: _fifo);
    }

    private void MaybeDuplicate(Message original)
    {
        var probability = World.Probability(FaultType.DuplicateDelivery);
        if (probability <= 0 || !World.Roll(probability)) return;
        var copy = new Message { Request = original.Request, Publish = original.Publish, Consumer = original.Consumer, Duplicate = true };
        K.AfterMs(Rng.LogNormal(_delivery), () => Enqueue(copy, front: false));
    }

    protected override (double Utilization, int Queue, int Instances) Sample() => (0, Backlog, _inFlight);
}

/// <summary>Tópico/barramento: fan-out para cada assinatura (fila ou push direto para computação, com retries).</summary>
internal sealed class TopicNode : SimNode, IBroker
{
    private readonly TokenBucket _bucket;
    private readonly bool _dlq;
    private readonly LatencyDist _latency;
    private readonly LatencyDist _delivery;

    public TopicNode(World world, Node node) : base(world, node)
    {
        var rate = Perf.MaxMessagesPerSecond ?? 100_000;
        _bucket = new TokenBucket(rate, rate);
        _dlq = Cfg.Bool("dlq.enabled");
        _latency = Perf.Latency ?? new LatencyDist(10, 50);
        _delivery = Perf.DeliveryLatency ?? new LatencyDist(25, 120);
    }

    public override void Handle(Exec exec, Action<Outcome> respond) => respond(Outcome.Success);

    public void Publish(Request request, CallNode publishCall, bool countsPath, Action<Outcome> ack)
    {
        if (!base.CanAccept())
        {
            ack(Unavailable());
            return;
        }

        Arrive();
        if (!_bucket.TryTake(K.Now, ThrottleFactor))
        {
            ack(Throttle());
            return;
        }

        var subscriptions = publishCall.Children
            .Where(c => c.Edge!.Interaction is Interaction.AsyncConsume or Interaction.AsyncPublish)
            .ToList();
        if (countsPath) World.AsyncStarted(request, subscriptions.Count);
        K.AfterMs(Rng.LogNormal(_latency), () => ack(Outcome.Success));
        foreach (var subscription in subscriptions)
            K.AfterMs(Rng.LogNormal(_delivery), () => Deliver(request, subscription, 1, duplicate: false));
    }

    private void Deliver(Request request, CallNode subscription, int attempt, bool duplicate)
    {
        var target = World.Nodes[subscription.Target.Id];
        if (subscription.Edge!.Interaction == Interaction.AsyncPublish && target is IBroker queue)
        {
            queue.Publish(request, subscription, false, ack =>
            {
                if (!ack.Ok) Retry(request, subscription, attempt, duplicate);
            });
            return;
        }

        var message = new Message { Request = request, Publish = subscription, Consumer = subscription, Duplicate = duplicate };
        var exec = new Exec(request, subscription, message, this);
        if (request.UseCase.Spec.RequiresOrdering) World.Integrity.RecordOrder(request, SequenceCheck(subscription));
        target.Handle(exec, outcome =>
        {
            var crashed = outcome.Ok && World.Roll(World.Probability(FaultType.ConsumerCrash));
            if (outcome.Ok && !crashed)
            {
                if (!duplicate)
                {
                    World.AsyncFinished(request, true);
                    var probability = World.Probability(FaultType.DuplicateDelivery);
                    if (probability > 0 && World.Roll(probability))
                        K.AfterMs(Rng.LogNormal(_delivery), () => Deliver(request, subscription, 1, duplicate: true));
                }

                return;
            }

            Retry(request, subscription, attempt, duplicate);
        });
    }

    private void Retry(Request request, CallNode subscription, int attempt, bool duplicate)
    {
        if (attempt < 8)
        {
            K.AfterMs(Math.Min(60_000, 1000 * Math.Pow(2, attempt - 1)), () => Deliver(request, subscription, attempt + 1, duplicate));
            return;
        }

        if (duplicate) return;
        if (_dlq)
        {
            DlqMessages++;
            if (request.Measured) World.Integrity.DlqMessages++;
        }
        else
        {
            World.LostEvent(request);
        }

        World.AsyncFinished(request, false);
    }
}

/// <summary>Stream particionado: ordem por chave, uma mensagem em processamento por partição e grupo de consumo.</summary>
internal sealed class StreamNode : SimNode, IBroker
{
    private sealed class PartitionState
    {
        public LinkedList<Message> Log { get; } = new();
        public bool InFlight { get; set; }
    }

    private readonly int _partitions;
    private readonly TokenBucket[] _buckets;
    private readonly Dictionary<string, PartitionState[]> _groups = new(StringComparer.Ordinal);
    private readonly LatencyDist _latency;
    private readonly LatencyDist _delivery;

    public StreamNode(World world, Node node) : base(world, node)
    {
        _partitions = Math.Max(1, Cfg.Int("partitions", 4));
        var perPartition = Perf.RecordsPerSecondPerPartition ?? 1000;
        _buckets = Enumerable.Range(0, _partitions).Select(_ => new TokenBucket(perPartition, perPartition)).ToArray();
        _latency = Perf.Latency ?? new LatencyDist(10, 45);
        _delivery = Perf.DeliveryLatency ?? new LatencyDist(60, 300);
    }

    public override void Handle(Exec exec, Action<Outcome> respond) => respond(Outcome.Success);

    public void Publish(Request request, CallNode publishCall, bool countsPath, Action<Outcome> ack)
    {
        if (!base.CanAccept())
        {
            ack(Unavailable());
            return;
        }

        Arrive();
        var partition = (int)(Rng.Fnv1a(request.Key) % (ulong)_partitions);
        if (!_buckets[partition].TryTake(K.Now, ThrottleFactor))
        {
            ack(Throttle());
            return;
        }

        var consumers = publishCall.Children.Where(c => c.Edge!.Interaction == Interaction.AsyncConsume).ToList();
        if (countsPath) World.AsyncStarted(request, consumers.Count);
        K.AfterMs(Rng.LogNormal(_latency), () => ack(Outcome.Success));
        foreach (var consumer in consumers)
        {
            var states = Group(consumer);
            var message = new Message { Request = request, Publish = publishCall, Consumer = consumer, Partition = partition, EnqueuedUs = K.Now };
            K.AfterMs(Rng.LogNormal(_delivery), () =>
            {
                states[partition].Log.AddLast(message);
                BacklogMax = Math.Max(BacklogMax, states.Sum(s => s.Log.Count));
                TryConsume(consumer, partition);
            });
        }
    }

    private PartitionState[] Group(CallNode consumer)
    {
        if (_groups.TryGetValue(consumer.Edge!.Id, out var states)) return states;
        states = Enumerable.Range(0, _partitions).Select(_ => new PartitionState()).ToArray();
        _groups[consumer.Edge.Id] = states;
        World.Nodes[consumer.Target.Id].OnCapacity(() =>
        {
            for (var p = 0; p < _partitions; p++) TryConsume(consumer, p);
        });
        return states;
    }

    private void TryConsume(CallNode consumerCall, int partition)
    {
        var state = Group(consumerCall)[partition];
        if (state.InFlight || state.Log.First is not { } head) return;
        var consumer = World.Nodes[consumerCall.Target.Id];
        if (!consumer.CanAccept()) return;

        var message = head.Value;
        state.InFlight = true;
        message.Deliveries++;
        OldestAgeMaxSeconds = Math.Max(OldestAgeMaxSeconds, (K.Now - message.EnqueuedUs) / 1_000_000.0);
        if (message.Request.UseCase.Spec.RequiresOrdering) World.Integrity.RecordOrder(message.Request, SequenceCheck(message.Consumer));
        var exec = new Exec(message.Request, consumerCall, message, this);
        consumer.Handle(exec, outcome =>
        {
            state.InFlight = false;
            var crashed = outcome.Ok && World.Roll(World.Probability(FaultType.ConsumerCrash));
            if (outcome.Ok && !crashed)
            {
                state.Log.RemoveFirst();
                if (!message.Duplicate) World.AsyncFinished(message.Request, true);
                var probability = World.Probability(FaultType.DuplicateDelivery);
                if (probability > 0 && World.Roll(probability))
                {
                    state.Log.AddFirst(new Message
                    {
                        Request = message.Request,
                        Publish = message.Publish,
                        Consumer = message.Consumer,
                        Partition = partition,
                        Duplicate = true,
                        EnqueuedUs = K.Now,
                    });
                }

                K.At(K.Now, () => TryConsume(consumerCall, partition));
                return;
            }

            if (message.Deliveries > 50)
            {
                state.Log.RemoveFirst();
                World.LostEvent(message.Request);
                if (!message.Duplicate) World.AsyncFinished(message.Request, false);
                K.At(K.Now, () => TryConsume(consumerCall, partition));
                return;
            }

            K.AfterMs(1000, () => TryConsume(consumerCall, partition));
        });
    }

    protected override (double Utilization, int Queue, int Instances) Sample() =>
        (0, _groups.Values.Sum(states => states.Sum(s => s.Log.Count)), _partitions);
}
