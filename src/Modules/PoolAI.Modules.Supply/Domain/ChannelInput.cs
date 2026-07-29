using System.Buffers;
using System.Text;

namespace PoolAI.Modules.Supply.Domain;

internal static class ChannelInput
{
    internal static string Name(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string normalized = value.Trim();
        if (!HasValidTextLength(normalized, minimum: 1, maximum: 100)
            || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A Channel name must contain between 1 and 100 non-control characters.",
                nameof(value));
        }

        return normalized;
    }

    internal static IReadOnlyList<ChannelModelMappingValue> ModelMappings(
        IEnumerable<ChannelModelMappingValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        List<ChannelModelMappingValue> normalized = [];
        HashSet<string> clientModels = new(StringComparer.Ordinal);
        foreach (ChannelModelMappingValue value in values)
        {
            string clientModel = ModelName(
                value.ClientModel,
                nameof(value.ClientModel));
            string upstreamModel = ModelName(
                value.UpstreamModel,
                nameof(value.UpstreamModel));
            if (!clientModels.Add(clientModel))
            {
                throw new ArgumentException(
                    "Channel client model mappings must be unique.",
                    nameof(values));
            }

            normalized.Add(new ChannelModelMappingValue(
                clientModel,
                upstreamModel));
        }

        if (normalized.Count == 0)
        {
            throw new ArgumentException(
                "A Channel requires at least one model mapping.",
                nameof(values));
        }

        normalized.Sort(static (left, right) => StringComparer.Ordinal.Compare(
            left.ClientModel,
            right.ClientModel));
        return normalized.AsReadOnly();
    }

    internal static void ExpectedVersion(long value) =>
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);

    internal static string Reason(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string normalized = value.Trim();
        if (!HasValidTextLength(normalized, minimum: 1, maximum: 500)
            || normalized.Any(static character => character is '\r' or '\n'))
        {
            throw new ArgumentException(
                "A reason must contain between 1 and 500 characters.",
                nameof(value));
        }

        return normalized;
    }

    internal static void ValidateMutation(
        ChannelResource current,
        ChannelResourceStatus? requestedStatus,
        IReadOnlyList<ChannelModelMappingValue>? requestedMappings)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.Status == ChannelResourceStatus.Retired)
        {
            throw new InvalidOperationException(
                "A retired Channel is terminal and cannot be changed.");
        }

        if (requestedStatus == ChannelResourceStatus.Retired)
        {
            throw new InvalidOperationException(
                "Channel retirement is only available through the retire command.");
        }

        if (requestedStatus == ChannelResourceStatus.Active
            && (requestedMappings ?? current.ModelMappings).Count == 0)
        {
            throw new InvalidOperationException(
                "An active Channel requires at least one model mapping.");
        }
    }

    internal static void ValidateRetirement(ChannelResource current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.Status == ChannelResourceStatus.Retired)
        {
            throw new InvalidOperationException(
                "A retired Channel is terminal.");
        }
    }

    internal static string ModelName(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value);
        string normalized = value.Trim();
        if (!HasValidTextLength(normalized, minimum: 1, maximum: 200)
            || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A model name must contain between 1 and 200 non-control characters.",
                parameterName);
        }

        return normalized;
    }

    private static bool HasValidTextLength(
        string value,
        int minimum,
        int maximum)
    {
        int count = 0;
        ReadOnlySpan<char> remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf16(
                remaining,
                out _,
                out int consumed);
            if (status != OperationStatus.Done)
            {
                return false;
            }

            count++;
            if (count > maximum)
            {
                return false;
            }

            remaining = remaining[consumed..];
        }

        return count >= minimum;
    }
}
