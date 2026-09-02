using System.Runtime.CompilerServices;

namespace PoolAI.EndToEndTests;

internal static class E2ETestProcessConfiguration
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // TestServer hosts use deterministic in-memory configuration and never
        // need file watchers. A pre-cancelled JSON reload token can otherwise
        // recurse synchronously in ChangeToken.OnChange before Program builds.
        // This environment change is isolated to the E2E testhost process.
        Environment.SetEnvironmentVariable(
            "DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE",
            "false");
    }
}
