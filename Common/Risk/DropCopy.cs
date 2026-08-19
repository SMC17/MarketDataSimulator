using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using MarketData.Common.Books;

namespace MarketData.Common.Risk
{
    /// <summary>One participant-visible event on its own activity.</summary>
    public readonly record struct DropCopyEvent(
        ulong Sequence,
        DateTime TimestampUtc,
        AuditEventType Type,
        string ParticipantId,
        int InstrumentId,
        Side Side,
        int Price,
        uint Quantity,
        RiskRejectReason Reason);

    /// <summary>
    /// A private feed of a participant's own orders and fills.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from market data because it answers a different question and has a different
    /// audience: not "what is the market doing" but "what did you do with what I sent". Firms
    /// reconcile against it, and compliance reads it.
    /// </para>
    /// <para>
    /// The one property that must not fail is isolation. A drop copy that leaks another
    /// participant's activity is a confidentiality breach, not a bug, so delivery is keyed by
    /// participant, subscribers can only ever be attached to their own stream, and a test asserts
    /// that a subscriber receives nothing belonging to anybody else.
    /// </para>
    /// </remarks>
    public sealed class DropCopyService
    {
        private readonly ConcurrentDictionary<string, List<Action<DropCopyEvent>>> _subscribers = new();
        private readonly PreTradeRiskGate _gate;
        private long _published;
        private long _suppressed;

        public DropCopyService(PreTradeRiskGate gate = null) => _gate = gate;

        public long Published => Interlocked.Read(ref _published);
        public long Suppressed => Interlocked.Read(ref _suppressed);

        /// <summary>
        /// Subscribes to one participant's own stream.
        /// </summary>
        /// <remarks>
        /// Refuses when the participant lacks the drop-copy entitlement, rather than accepting the
        /// subscription and delivering nothing - a silent empty stream is indistinguishable from a
        /// quiet one, and the subscriber would never find out.
        /// </remarks>
        public IDisposable Subscribe(string participantId, Action<DropCopyEvent> handler)
        {
            ArgumentNullException.ThrowIfNull(participantId);
            ArgumentNullException.ThrowIfNull(handler);

            if (_gate is not null && !_gate.IsEntitled(participantId, 0, Entitlement.DropCopy))
                throw new InvalidOperationException(
                    $"{participantId} is not entitled to a drop copy.");

            var handlers = _subscribers.GetOrAdd(participantId, _ => new List<Action<DropCopyEvent>>());

            lock (handlers)
                handlers.Add(handler);

            return new Subscription(this, participantId, handler);
        }

        public void Publish(in DropCopyEvent copy)
        {
            if (string.IsNullOrEmpty(copy.ParticipantId) ||
                (_gate is not null &&
                 !_gate.IsEntitled(copy.ParticipantId, copy.InstrumentId, Entitlement.DropCopy)))
            {
                Interlocked.Increment(ref _suppressed);
                return;
            }

            if (!_subscribers.TryGetValue(copy.ParticipantId, out var handlers))
            {
                Interlocked.Increment(ref _suppressed);
                return;
            }

            Action<DropCopyEvent>[] snapshot;

            lock (handlers)
                snapshot = handlers.ToArray();

            foreach (var handler in snapshot)
                handler(copy);

            Interlocked.Increment(ref _published);
        }

        private void Unsubscribe(string participantId, Action<DropCopyEvent> handler)
        {
            if (!_subscribers.TryGetValue(participantId, out var handlers))
                return;

            lock (handlers)
                handlers.Remove(handler);
        }

        private sealed class Subscription : IDisposable
        {
            private readonly DropCopyService _service;
            private readonly string _participantId;
            private readonly Action<DropCopyEvent> _handler;
            private bool _disposed;

            public Subscription(DropCopyService service, string participantId, Action<DropCopyEvent> handler)
            {
                _service = service;
                _participantId = participantId;
                _handler = handler;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;
                _service.Unsubscribe(_participantId, _handler);
            }
        }
    }
}
