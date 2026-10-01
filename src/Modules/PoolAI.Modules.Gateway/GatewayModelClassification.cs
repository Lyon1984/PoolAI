using System.Text.Json;
using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Gateway.Application;

public enum GatewayModelClassification
{
    Quarantine,
    Absent,
    DeclaredFalse,
    DeclaredTrue,
}
