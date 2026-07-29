namespace PoolAI.Modules.Supply.Domain;

internal static class GroupSupplyInput
{
    internal static IReadOnlyList<GroupSupplyBindingValue> Bindings(
        IEnumerable<GroupSupplyBindingValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        List<GroupSupplyBindingValue> normalized = [];
        HashSet<Guid> accountIds = [];
        foreach (GroupSupplyBindingValue value in values)
        {
            if (!accountIds.Add(value.AccountId.Value))
            {
                throw new ArgumentException(
                    "A Group Supply Configuration cannot contain duplicate Account bindings.",
                    nameof(values));
            }

            if (value.PriorityOverride is < -100000 or > 100000)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(values),
                    "A binding priority override must be between -100000 and 100000.");
            }

            if (value.WeightOverride is < 1 or > 100000)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(values),
                    "A binding weight override must be between 1 and 100000.");
            }

            normalized.Add(value);
        }

        normalized.Sort(static (left, right) =>
            left.AccountId.Value.CompareTo(right.AccountId.Value));
        return normalized.AsReadOnly();
    }

    internal static void ExpectedVersion(long value) =>
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);

    internal static string Reason(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string normalized = value.Trim();
        if (normalized.Length is < 1 or > 500
            || normalized.Any(static character => character is '\r' or '\n'))
        {
            throw new ArgumentException(
                "A reason must contain between 1 and 500 characters.",
                nameof(value));
        }

        return normalized;
    }
}
