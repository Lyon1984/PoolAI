namespace PoolAI.Modules.Gateway.Application;

public sealed class GatewayAdmissionOptions
{
    public const int DefaultDataNonStreamPermits = 200;
    public const int DefaultDataStreamPermits = 600;
    public const int DefaultDataQueueLimit = 0;
    public const int DefaultControlPermits = 100;
    public const int DefaultControlQueueLimit = 50;
    public const int DefaultUsagePermits = 100;
    public const int DefaultUsageQueueLimit = 20;

    public GatewayAdmissionOptions(
        int dataNonStreamPermits = DefaultDataNonStreamPermits,
        int dataStreamPermits = DefaultDataStreamPermits,
        int dataQueueLimit = DefaultDataQueueLimit,
        int controlPermits = DefaultControlPermits,
        int controlQueueLimit = DefaultControlQueueLimit,
        int usagePermits = DefaultUsagePermits,
        int usageQueueLimit = DefaultUsageQueueLimit)
    {
        ValidatePermits(
            dataNonStreamPermits,
            maximum: 10_000,
            parameterName: nameof(dataNonStreamPermits));
        ValidatePermits(
            dataStreamPermits,
            maximum: 10_000,
            parameterName: nameof(dataStreamPermits));
        if (dataQueueLimit != DefaultDataQueueLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dataQueueLimit),
                "The R1.1 NonStream and SSE bulkheads must fail fast.");
        }

        ValidatePermits(
            controlPermits,
            maximum: 1_000,
            parameterName: nameof(controlPermits));
        ArgumentOutOfRangeException.ThrowIfNegative(controlQueueLimit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            controlQueueLimit,
            DefaultControlQueueLimit);
        ValidatePermits(
            usagePermits,
            maximum: 1_000,
            parameterName: nameof(usagePermits));
        ArgumentOutOfRangeException.ThrowIfNegative(usageQueueLimit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            usageQueueLimit,
            DefaultUsageQueueLimit);

        DataNonStreamPermits = dataNonStreamPermits;
        DataStreamPermits = dataStreamPermits;
        DataQueueLimit = dataQueueLimit;
        ControlPermits = controlPermits;
        ControlQueueLimit = controlQueueLimit;
        UsagePermits = usagePermits;
        UsageQueueLimit = usageQueueLimit;
    }

    public int DataNonStreamPermits { get; }

    public int DataStreamPermits { get; }

    public int DataQueueLimit { get; }

    public int ControlPermits { get; }

    public int ControlQueueLimit { get; }

    public int UsagePermits { get; }

    public int UsageQueueLimit { get; }

    private static void ValidatePermits(
        int permits,
        int maximum,
        string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permits, 1, parameterName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            permits,
            maximum,
            parameterName);
    }
}
