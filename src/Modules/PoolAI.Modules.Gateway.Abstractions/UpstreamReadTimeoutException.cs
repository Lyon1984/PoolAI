namespace PoolAI.Modules.Gateway.Abstractions;

public sealed class UpstreamReadTimeoutException : IOException
{
    public UpstreamReadTimeoutException(string code, Exception? innerException = null)
        : base("The upstream response read deadline expired.", innerException)
    {
        if (code is not ("upstream_first_byte_timeout" or "upstream_stream_idle_timeout"))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        Code = code;
    }

    public string Code { get; }
}
