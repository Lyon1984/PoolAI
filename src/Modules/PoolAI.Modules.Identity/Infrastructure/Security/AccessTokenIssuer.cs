#pragma warning disable MA0048 // Access-token options are private implementation details of this issuer.
using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application.Ports;

namespace PoolAI.Modules.Identity.Infrastructure.Security;

internal sealed class AccessTokenIssuer : IAccessTokenIssuer
{
    private static readonly string Header = Base64Url.Encode(
        "{\"alg\":\"HS256\",\"typ\":\"JWT\"}"u8);
    private readonly AccessTokenOptions _options;
    private readonly TimeProvider _timeProvider;

    internal AccessTokenIssuer(
        AccessTokenOptions options,
        TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public AccessTokenSecret Issue(AccessTokenSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (subject.TokenVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(subject),
                "Access-token version must be positive.");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        long issuedAt = now.ToUnixTimeSeconds();
        long expiresAt = checked(issuedAt + (long)_options.Lifetime.TotalSeconds);
        byte[] payloadBytes = BuildPayload(subject, issuedAt, expiresAt, now);
        string payload;
        try
        {
            payload = Base64Url.Encode(payloadBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadBytes);
        }

        string signingInput = string.Concat(Header, ".", payload);
        byte[] signingBytes = Encoding.ASCII.GetBytes(signingInput);
        byte[] signature = HMACSHA256.HashData(_options.SigningKey, signingBytes);
        try
        {
            return new AccessTokenSecret(
                string.Concat(signingInput, ".", Base64Url.Encode(signature)),
                DateTimeOffset.FromUnixTimeSeconds(expiresAt));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingBytes);
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private byte[] BuildPayload(
        AccessTokenSubject subject,
        long issuedAt,
        long expiresAt,
        DateTimeOffset now)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("iss", _options.Issuer);
            writer.WriteString("aud", _options.Audience);
            writer.WriteString("sub", subject.UserId.Value);
            writer.WriteString("role", RoleCode(subject.Role));
            writer.WriteNumber("token_version", subject.TokenVersion);
            writer.WriteString("sid", subject.SessionFamilyId.Value);
            writer.WriteString("jti", Guid.CreateVersion7(now));
            writer.WriteNumber("iat", issuedAt);
            writer.WriteNumber("nbf", issuedAt);
            writer.WriteNumber("exp", expiresAt);
            writer.WriteEndObject();
        }

        byte[] result = buffer.WrittenSpan.ToArray();
        buffer.Clear();
        return result;
    }

    private static string RoleCode(SystemRole role) => role switch
    {
        SystemRole.Admin => "admin",
        SystemRole.Operator => "operator",
        SystemRole.Auditor => "auditor",
        SystemRole.User => "user",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
}

internal sealed class AccessTokenOptions
{
    internal static readonly TimeSpan FrozenLifetime = TimeSpan.FromMinutes(15);

    internal AccessTokenOptions(
        string issuer,
        string audience,
        TimeSpan lifetime,
        byte[] signingKey)
    {
        ValidateName(issuer, nameof(issuer));
        ValidateName(audience, nameof(audience));
        if (lifetime != FrozenLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime),
                "The R1 access-token lifetime must be 15 minutes.");
        }

        ArgumentNullException.ThrowIfNull(signingKey);
        if (signingKey.Length < 32)
        {
            throw new ArgumentException(
                "The JWT signing key must contain at least 256 bits.",
                nameof(signingKey));
        }

        Issuer = issuer;
        Audience = audience;
        Lifetime = lifetime;
        SigningKey = signingKey;
    }

    internal string Issuer { get; }

    internal string Audience { get; }

    internal TimeSpan Lifetime { get; }

    internal byte[] SigningKey { get; }

    internal static AccessTokenOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string issuer = configuration["Auth:Jwt:Issuer"] ?? "PoolAI";
        string audience = configuration["Auth:Jwt:Audience"] ?? "PoolAI.Web";
        int lifetimeMinutes = configuration.GetValue(
            "Auth:Jwt:AccessTokenMinutes",
            15);
        byte[] signingKey;
        try
        {
            signingKey = Convert.FromBase64String(
                configuration["Auth:Jwt:SigningKey"] ?? string.Empty);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("JWT signing-key configuration is invalid.", exception);
        }

        try
        {
            return new AccessTokenOptions(
                issuer,
                audience,
                TimeSpan.FromMinutes(lifetimeMinutes),
                signingKey);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("JWT access-token configuration is invalid.", exception);
        }
    }

    private static void ValidateName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Length > 128
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "JWT issuer and audience must be canonical 1..128 character values.",
                parameterName);
        }
    }
}
#pragma warning restore MA0048
