namespace PoolAI.Modules.Supply.Application.Ports;

internal enum AccountCredentialRewrapWriteDisposition
{
    Rewrapped,
    ValidationFailed,
    NotFound,
    CredentialRevisionConflict,
    ContentMismatch,
}
