using PoolAI.Modules.Identity.Application.Ports;

namespace PoolAI.Modules.Identity.Infrastructure.Security;

internal static class TotpIdempotencyResponseAad
{
    internal static string Build(
        string field,
        EntityId oneTimeTokenId,
        IdempotencySecretBinding binding) => IdempotencyResponseAad.Build(
            field,
            oneTimeTokenId,
            binding);
}
