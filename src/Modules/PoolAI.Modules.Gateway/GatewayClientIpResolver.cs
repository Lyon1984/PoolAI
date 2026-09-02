using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PoolAI.Modules.Gateway.Application;

public sealed class GatewayClientIpResolver
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly GatewayIngressOptions _options;

    public GatewayClientIpResolver(GatewayIngressOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public bool TryResolveClientAddress(
        IPAddress? socketPeer,
        IReadOnlyList<string>? forwardedForFieldValues,
        out IPAddress? clientAddress)
    {
        clientAddress = null;
        if (socketPeer is null)
        {
            return false;
        }

        IPAddress current = GatewayIpCidr.NormalizeMappedAddress(socketPeer);
        if (current.AddressFamily is not (
            AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            return false;
        }

        if (!_options.IsTrusted(current))
        {
            clientAddress = current;
            return true;
        }

        if (forwardedForFieldValues is null
            || forwardedForFieldValues.Count != 1
            || !TryParseForwardedFor(
                forwardedForFieldValues[0],
                out IReadOnlyList<IPAddress>? hops))
        {
            return false;
        }

        IReadOnlyList<IPAddress> parsedHops = hops!;
        for (int index = parsedHops.Count - 1; index >= 0; index--)
        {
            if (!_options.IsTrusted(current))
            {
                break;
            }

            current = parsedHops[index];
        }

        clientAddress = current;
        return true;
    }

    public bool TryResolveAuthorizedClientAddress(
        IPAddress? socketPeer,
        IReadOnlyList<string>? forwardedForFieldValues,
        IReadOnlyList<string>? allowedCidrs,
        out IPAddress? clientAddress)
    {
        clientAddress = null;
        if (!TryResolveClientAddress(
                socketPeer,
                forwardedForFieldValues,
                out IPAddress? resolved)
            || !IsAllowedByApiKeyCidrs(resolved, allowedCidrs))
        {
            return false;
        }

        clientAddress = resolved;
        return true;
    }

    public static bool IsAllowedByApiKeyCidrs(
        IPAddress? clientAddress,
        IReadOnlyList<string>? allowedCidrs)
    {
        if (clientAddress is null || allowedCidrs is null)
        {
            return false;
        }

        if (allowedCidrs.Count == 0)
        {
            return true;
        }

        bool matched = false;
        foreach (string value in allowedCidrs)
        {
            if (!GatewayIpCidr.TryParseCanonical(
                    value,
                    allowZeroPrefix: true,
                    out GatewayIpCidr? cidr))
            {
                return false;
            }

            matched |= cidr!.Contains(clientAddress);
        }

        return matched;
    }

    public override string ToString() => nameof(GatewayClientIpResolver);

    private bool TryParseForwardedFor(
        string? fieldValue,
        out IReadOnlyList<IPAddress>? hops)
    {
        hops = null;
        if (string.IsNullOrEmpty(fieldValue)
            || !HasValidUtf8Length(fieldValue))
        {
            return false;
        }

        string[] tokens = fieldValue.Split(',', StringSplitOptions.None);
        if (tokens.Length is < 1
            || tokens.Length > _options.ForwardedForLimit)
        {
            return false;
        }

        IPAddress[] parsed = new IPAddress[tokens.Length];
        for (int index = 0; index < tokens.Length; index++)
        {
            ReadOnlySpan<char> token = TrimOptionalWhitespace(
                tokens[index].AsSpan());
            if (!GatewayIpCidr.TryParseCanonicalAddress(
                    token,
                    allowMappedIpv6: true,
                    out IPAddress? address))
            {
                return false;
            }

            parsed[index] = address!;
        }

        hops = parsed;
        return true;
    }

    private static bool HasValidUtf8Length(string value)
    {
        try
        {
            return StrictUtf8.GetByteCount(value)
                <= GatewayIngressOptions.MaximumForwardedForUtf8Bytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static ReadOnlySpan<char> TrimOptionalWhitespace(
        ReadOnlySpan<char> value)
    {
        int start = 0;
        while (start < value.Length && value[start] is ' ' or '\t')
        {
            start++;
        }

        int end = value.Length;
        while (end > start && value[end - 1] is ' ' or '\t')
        {
            end--;
        }

        return value[start..end];
    }
}
