using System.Text;

namespace PoolAI.Infrastructure.Secrets;

public sealed record SecretEnvelopeContext
{
    private const int MaxComponentLength = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public SecretEnvelopeContext(
        string purpose,
        string entityType,
        string entityId,
        string fieldName)
    {
        Purpose = ValidateComponent(purpose, nameof(purpose));
        EntityType = ValidateComponent(entityType, nameof(entityType));
        EntityId = ValidateComponent(entityId, nameof(entityId));
        FieldName = ValidateComponent(fieldName, nameof(fieldName));
    }

    public string Purpose { get; }

    public string EntityType { get; }

    public string EntityId { get; }

    public string FieldName { get; }

    public static SecretEnvelopeContext Parse(string canonicalAad)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalAad);
        string[] components = canonicalAad.Split('|');
        if (components.Length != 6
            || !string.Equals(components[0], "poolai", StringComparison.Ordinal)
            || !string.Equals(components[1], "v1", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The secret envelope context is not canonical.",
                nameof(canonicalAad));
        }

        return new SecretEnvelopeContext(
            components[2],
            components[3],
            components[4],
            components[5]);
    }

    internal string CanonicalAad =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"poolai|v1|{Purpose}|{EntityType}|{EntityId}|{FieldName}");

    public override string ToString() => nameof(SecretEnvelopeContext);

    private static string ValidateComponent(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > MaxComponentLength
            || value.Contains('|', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Secret envelope context components are not canonical.",
                parameterName);
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException(
                "Secret envelope context components must contain valid Unicode scalar values.",
                parameterName);
        }

        return value;
    }
}
