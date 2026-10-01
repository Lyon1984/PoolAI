using System.Text.Json;
using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Gateway.Application;

public sealed class GatewayReplayStorageException : IOException
{
    public GatewayReplayStorageException() : base("Private model replay storage is unavailable.") { }
}
