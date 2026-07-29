namespace PoolAI.Modules.Supply.Application.Ports;

internal enum AccountCredentialReplacementDisposition
{
    Replaced,
    ValidationFailed,
    NotFound,
    AccountRetired,
    VersionConflict,
}
