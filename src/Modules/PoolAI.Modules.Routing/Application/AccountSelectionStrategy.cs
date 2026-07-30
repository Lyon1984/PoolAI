using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.Modules.Routing.Application;

internal static class AccountSelectionStrategy
{
    public static IReadOnlyList<AccountCandidate> Order(
        IReadOnlyList<AccountCandidate> candidates,
        RouteAccountCommand command,
        EntityId? stickyAccountId)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(command);

        List<AccountCandidate> ordered = candidates
            .OrderByDescending(static candidate => candidate.Priority)
            .ThenBy(candidate => WeightedScore(candidate, command))
            .ThenBy(static candidate => candidate.AccountId.Value)
            .ToList();
        if (stickyAccountId is not { } accountId)
        {
            return ordered;
        }

        int index = ordered.FindIndex(candidate => candidate.AccountId == accountId);
        if (index <= 0)
        {
            return ordered;
        }

        AccountCandidate sticky = ordered[index];
        ordered.RemoveAt(index);
        ordered.Insert(0, sticky);
        return ordered;
    }

    private static ulong WeightedScore(
        AccountCandidate candidate,
        RouteAccountCommand command)
    {
        string seed = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{command.GroupId.Value:D}|{command.RequestId.Value:D}|{command.AttemptId.Value:D}|{command.Model}|{candidate.AccountId.Value:D}");
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        ulong hash = BinaryPrimitives.ReadUInt64BigEndian(digest);
        int effectiveWeight = candidate.Health == AccountHealth.Degraded
            ? Math.Max(1, (candidate.Weight + 1) / 2)
            : candidate.Weight;
        return hash / checked((ulong)effectiveWeight);
    }
}
