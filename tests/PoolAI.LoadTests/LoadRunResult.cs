namespace PoolAI.LoadTests;

internal sealed record LoadRunResult(int Scheduled, int Completed, int PeakConcurrency);
