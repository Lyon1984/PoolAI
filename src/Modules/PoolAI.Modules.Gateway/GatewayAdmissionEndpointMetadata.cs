namespace PoolAI.Modules.Gateway.Application;

/// <summary>
/// Lets a mapped data-plane endpoint declare its admission partition without
/// making the admission middleware inspect or deserialize the request body.
/// </summary>
public sealed record GatewayAdmissionEndpointMetadata(GatewayAdmissionKind Kind);
