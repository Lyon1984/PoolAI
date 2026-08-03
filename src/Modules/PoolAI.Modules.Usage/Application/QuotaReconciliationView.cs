using System.Numerics;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.Usage.Application;

internal sealed record QuotaReconciliationView(
    GroupQuotaReconciliationFactSnapshot Authoritative,
    BigInteger ConsumedVariance,
    BigInteger ReservedVariance,
    UsageProjectionReconciliation UsageProjection);
