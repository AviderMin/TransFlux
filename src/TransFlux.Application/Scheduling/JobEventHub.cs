using System.Threading.Channels;
using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;

namespace TransFlux.Application.Scheduling;

/// <summary>
/// In-memory broadcast hub for <see cref="JobEvent"/> instances. Every published event receives
/// the next global monotonic sequence number, then is fanned out to all live subscriptions.
/// Subscriptions are created atomically with the current sequence watermark so that
/// "snapshot first, then live events with Sequence &gt; watermark" cannot miss events.
/// </summary>
internal sealed class JobEventHub
{
    internal sealed class Subscription
    {
        public readonly Channel<JobEvent> Channel =
            System.Threading.Channels.Channel.CreateUnbounded<JobEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
    }

    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = new();
    private long _sequence;

    internal long CurrentSequence
    {
        get { lock (_gate) { return _sequence; } }
    }

    /// <summary>Registers a subscriber and returns the sequence watermark observed at subscription time.</summary>
    internal SubscriptionHandle Subscribe()
    {
        lock (_gate)
        {
            var sub = new Subscription();
            _subscriptions.Add(sub);
            return new SubscriptionHandle(this, sub, _sequence);
        }
    }

    internal void Unsubscribe(Subscription sub)
    {
        lock (_gate)
        {
            _subscriptions.Remove(sub);
        }
        sub.Channel.Writer.TryComplete();
    }

    /// <summary>Publishes an event to every current subscriber and returns the assigned sequence.</summary>
    internal long Publish(JobEvent evt)
    {
        lock (_gate)
        {
            _sequence++;
            var stamped = evt with { Sequence = _sequence };
            foreach (var sub in _subscriptions)
            {
                sub.Channel.Writer.TryWrite(stamped);
            }
            return _sequence;
        }
    }

    /// <summary>Completes all subscriptions (used on shutdown).</summary>
    internal void CompleteAll()
    {
        List<Subscription> copy;
        lock (_gate)
        {
            copy = new List<Subscription>(_subscriptions);
            _subscriptions.Clear();
        }
        foreach (var sub in copy)
        {
            sub.Channel.Writer.TryComplete();
        }
    }

    internal sealed class SubscriptionHandle
    {
        private readonly JobEventHub _hub;
        private readonly Subscription _subscription;

        public SubscriptionHandle(JobEventHub hub, Subscription subscription, long startSequence)
        {
            _hub = hub;
            _subscription = subscription;
            StartSequence = startSequence;
        }

        public long StartSequence { get; }

        public ChannelReader<JobEvent> Reader => _subscription.Channel.Reader;

        public void Dispose() => _hub.Unsubscribe(_subscription);
    }
}