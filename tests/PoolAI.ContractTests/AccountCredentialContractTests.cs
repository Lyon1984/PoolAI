using System.Reflection;
using System.Text.Json.Serialization;
using PoolAI.Contracts.Generated;

namespace PoolAI.ContractTests;

public sealed class AccountCredentialContractTests
{
    [Fact]
    public void AccountCredentialIsWriteOnlyAndAbsentFromEveryAccountResponse()
    {
        Type[] generatedTypes = typeof(Account).Assembly
            .GetTypes()
            .Where(static type =>
                type.IsClass
                && type.IsPublic
                && string.Equals(
                    type.Namespace,
                    typeof(Account).Namespace,
                    StringComparison.Ordinal))
            .ToArray();

        string[] credentialWriters = generatedTypes
            .Where(static type => JsonPropertyNames(type).Contains(
                "credential",
                StringComparer.Ordinal))
            .Select(static type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[] { nameof(AccountCreateRequest), nameof(AccountUpdateRequest) },
            credentialWriters);

        string[] forbiddenSecretProperties =
        [
            "credential",
            "credential_envelope",
            "wrapped_dek",
            "wrap_nonce",
            "wrap_tag",
            "ciphertext",
            "nonce",
            "tag",
        ];
        Type[] accountResponseTypes = generatedTypes
            .Where(static type =>
                type.Name.StartsWith("Account", StringComparison.Ordinal)
                && type != typeof(AccountCreateRequest)
                && type != typeof(AccountUpdateRequest))
            .ToArray();
        foreach (Type responseType in accountResponseTypes)
        {
            Assert.Empty(JsonPropertyNames(responseType).Intersect(
                forbiddenSecretProperties,
                StringComparer.Ordinal));
        }

        string openApi = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "contracts",
            "openapi-v1.yaml"));
        AssertWriteOnlyCredentialSchema(
            SliceSchema(openApi, "AccountCreateRequest", "AccountUpdateRequest"));
        AssertWriteOnlyCredentialSchema(
            SliceSchema(openApi, "AccountUpdateRequest", "AccountPage"));
    }

    private static void AssertWriteOnlyCredentialSchema(string schema)
    {
        const string credentialContract =
            "        credential:\n"
            + "          type: string\n"
            + "          minLength: 16\n"
            + "          maxLength: 4096\n"
            + "          writeOnly: true";
        Assert.Contains(credentialContract, schema, StringComparison.Ordinal);
    }

    private static string SliceSchema(
        string openApi,
        string schemaName,
        string nextSchemaName)
    {
        string startMarker = $"    {schemaName}:";
        string endMarker = $"    {nextSchemaName}:";
        int start = openApi.IndexOf(startMarker, StringComparison.Ordinal);
        int end = openApi.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing OpenAPI schema {schemaName}.");
        Assert.True(end > start, $"Missing OpenAPI schema boundary {nextSchemaName}.");
        return openApi[start..end];
    }

    private static string[] JsonPropertyNames(Type type) =>
        type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(static property => property
                .GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .Where(static name => name is not null)
            .Select(static name => name!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PoolAI.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the PoolAI repository root.");
    }
}
