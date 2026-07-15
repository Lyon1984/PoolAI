namespace PoolAI.BuildingBlocks;

public sealed class ReleaseManifestValidationException : Exception
{
    public ReleaseManifestValidationException(string message)
        : base(message)
    {
    }

    public ReleaseManifestValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
