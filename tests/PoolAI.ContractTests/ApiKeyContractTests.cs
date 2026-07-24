using System.Reflection;
using System.Text.Json.Serialization;
using PoolAI.Contracts.Generated;

namespace PoolAI.ContractTests;

public sealed class ApiKeyContractTests
{
    [Fact]
    public void ApiKeyGroupBindingHasNoMutationContract()
    {
        // Governing contract: AC-007 and ADR 0007 require Group selection only
        // when a Key is created. Changing Group requires revoke plus create.
        Assert.Contains("group_id", JsonPropertyNames<ApiKeyCreateRequest>());
        Assert.Contains("group_id", JsonPropertyNames<AdminUserApiKeyCreateRequest>());
        Assert.Contains("group_id", JsonPropertyNames<ApiKey>());

        Assert.DoesNotContain("group_id", JsonPropertyNames<ApiKeyUpdateRequest>());
        Assert.DoesNotContain("group_id", JsonPropertyNames<AdminUserApiKeyUpdateRequest>());
        Assert.DoesNotContain("group_id", JsonPropertyNames<ApiKeyRotateRequest>());
    }

    private static string[] JsonPropertyNames<T>() =>
        typeof(T)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(static property => property
                .GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .Where(static name => name is not null)
            .Select(static name => name!)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
