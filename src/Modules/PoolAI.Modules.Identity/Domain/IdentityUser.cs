using PoolAI.Modules.Identity.Abstractions;

namespace PoolAI.Modules.Identity.Domain;

internal sealed record IdentityUser(
    EntityId Id,
    string Email,
    string NormalizedEmail,
    string DisplayName,
    string PasswordHash,
    SystemRole Role,
    UserLifecycle Status,
    long TokenVersion,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal Application.UserView ToView() => new(
        Id,
        Email,
        DisplayName,
        Role,
        Status,
        Version,
        CreatedAt,
        UpdatedAt);
}
