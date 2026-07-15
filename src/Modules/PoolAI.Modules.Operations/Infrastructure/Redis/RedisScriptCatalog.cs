using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Operations.Infrastructure.Redis;

internal sealed class RedisScriptCatalog
{
    internal RedisScriptCatalog(IReadOnlyList<RedisScriptAsset> scripts)
    {
        Scripts = scripts;
    }

    public IReadOnlyList<RedisScriptAsset> Scripts { get; }

    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "Redis SCRIPT LOAD/EVALSHA uses SHA-1 as a content-addressed cache identifier; release integrity is independently enforced with SHA-256.")]
    public static RedisScriptCatalog Load(
        ReleaseManifestRedisV1 manifest,
        Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(assembly);

        string[] resourceNames = assembly.GetManifestResourceNames();
        List<RedisScriptAsset> scripts = new(manifest.Scripts.Count);
        foreach (ReleaseManifestRedisScriptV1 script in manifest.Scripts)
        {
            string fileName = Path.GetFileName(script.Source);
            string resourceName = resourceNames.SingleOrDefault(candidate =>
                    candidate.EndsWith($".RedisScripts.{fileName}", StringComparison.Ordinal))
                ?? throw new ReleaseManifestValidationException(
                    "A Redis script resource declared by the release manifest is missing.");
            using Stream resource = assembly.GetManifestResourceStream(resourceName)
                ?? throw new ReleaseManifestValidationException(
                    "A Redis script resource declared by the release manifest is missing.");
            using MemoryStream buffer = new();
            resource.CopyTo(buffer);
            byte[] bodyBytes = buffer.ToArray();
            string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(bodyBytes));
            if (!string.Equals(actualSha256, script.BodySha256, StringComparison.Ordinal))
            {
                throw new ReleaseManifestValidationException(
                    "A Redis script resource does not match its release manifest checksum.");
            }

            string body = new UTF8Encoding(false, true).GetString(bodyBytes);
            scripts.Add(new RedisScriptAsset(
                script.Name,
                script.LogicalVersion,
                script.BodySha256,
                SHA1.HashData(bodyBytes),
                body));
        }

        return new RedisScriptCatalog(scripts);
    }
}
