namespace PoolAI.Modules.Identity.Abstractions;

public sealed record EmailOutboxFailureMetric(
    string FailureClass,
    string Outcome,
    string TerminalReason,
    long Count);
