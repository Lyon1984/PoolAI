using System.Text.Json;

namespace PoolAI.ContractTests;

public sealed class QuotaReconciliationAlertRuleContractTests
{
    [Fact]
    public void ProjectionMismatchRequiresFiveMinutesAndNotifiesRecovery()
    {
        string root = FindRepositoryRoot();
        string rulePath = Path.Combine(
            root,
            "ops",
            "monitoring",
            "quota-reconciliation-alert-rules-v1.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(rulePath));
        JsonElement policy = document.RootElement;

        Assert.Equal(1, policy.GetProperty("schema_version").GetInt32());
        Assert.InRange(
            policy.GetProperty("evaluation_interval_seconds").GetInt32(),
            1,
            30);
        JsonElement rule = Assert.Single(
            policy.GetProperty("rules").EnumerateArray().ToArray());
        Assert.Equal(
            "quota_projection_mismatch_sustained",
            rule.GetProperty("id").GetString());
        Assert.Equal(
            "poolai_quota_reconciliation_mismatched_groups",
            rule.GetProperty("metric").GetString());
        JsonProperty label = Assert.Single(
            rule.GetProperty("match_labels").EnumerateObject().ToArray());
        Assert.Equal("kind", label.Name);
        Assert.Equal("projection", label.Value.GetString());
        Assert.Equal("greater_than", rule.GetProperty("comparison").GetString());
        Assert.Equal(0, rule.GetProperty("threshold").GetInt64());
        Assert.Equal(300, rule.GetProperty("hold_seconds").GetInt32());
        Assert.Equal("P1", rule.GetProperty("severity").GetString());
        Assert.Equal("projection", rule.GetProperty("layer").GetString());
        Assert.True(rule.GetProperty("notify_on_resolved").GetBoolean());

        string runbook = Assert.IsType<string>(
            rule.GetProperty("runbook").GetString());
        Assert.Equal("ops/runbooks/quota-reconciliation.md", runbook);
        Assert.True(File.Exists(Path.Combine(root, runbook)));
        Assert.DoesNotContain("group_id", File.ReadAllText(rulePath), StringComparison.Ordinal);
        Assert.DoesNotContain("period_id", File.ReadAllText(rulePath), StringComparison.Ordinal);
    }

    [Fact]
    public void RunbookFreezesInboxProofAndOneShotRecoveryControls()
    {
        string runbook = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "ops",
            "runbooks",
            "quota-reconciliation.md"));

        Assert.Contains("consumer `usage-hourly-v1`", runbook, StringComparison.Ordinal);
        Assert.Contains("topic `poolai.quota.v1`", runbook, StringComparison.Ordinal);
        Assert.Contains("physical Outbox `event_sequence`", runbook, StringComparison.Ordinal);
        Assert.Contains("schema version `1`", runbook, StringComparison.Ordinal);
        Assert.Contains(
            "WorkerJobs__UsageRebuild__Enabled=true",
            runbook,
            StringComparison.Ordinal);
        Assert.Contains(
            "WorkerJobs__UsageRebuild__Enabled=false",
            runbook,
            StringComparison.Ordinal);
        Assert.Contains("returns exit code `0` only", runbook, StringComparison.Ordinal);
        Assert.Contains("current ETag", runbook, StringComparison.Ordinal);
        Assert.Contains("idempotency key", runbook, StringComparison.Ordinal);
        Assert.Contains("Do not run ad-hoc `UPDATE`", runbook, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PoolAI.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
