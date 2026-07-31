using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PoolAI.Modules.GroupQuota;
using PoolAI.Modules.GroupQuota.Application;

namespace PoolAI.UnitTests;

public sealed class GroupQuotaDependencyInjectionTests
{
    [Fact]
    public void ValidRequestHashPepperBuildsPolicy()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Idempotency:RequestHashPepper"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddGroupQuotaModule();
        using ServiceProvider serviceProvider = services.BuildServiceProvider();

        GroupQuotaPolicy policy = serviceProvider.GetRequiredService<GroupQuotaPolicy>();

        Assert.Equal(32, policy.RequestHashPepper.Length);
    }

    [Theory]
    [InlineData("not-base64", "Idempotency:RequestHashPepper is invalid.")]
    [InlineData("AA==", "Idempotency:RequestHashPepper must contain at least 256 bits.")]
    public void InvalidRequestHashPepperFailsClosedWhenPolicyIsResolved(
        string encodedPepper,
        string expectedMessage)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Idempotency:RequestHashPepper"] = encodedPepper,
            })
            .Build();
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddGroupQuotaModule();
        using ServiceProvider serviceProvider = services.BuildServiceProvider();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            serviceProvider.GetRequiredService<GroupQuotaPolicy>);

        Assert.StartsWith(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotaPeriodUseCasesAreRegisteredAsSingletonPorts()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Idempotency:RequestHashPepper"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddGroupQuotaModule();

        foreach (Type port in new[]
                 {
                     typeof(IGetGroupQuotaUseCase),
                     typeof(IAuthorizeQuotaMutationUseCase),
                     typeof(IAdjustGroupQuotaUseCase),
                     typeof(IResetGroupQuotaUseCase),
                 })
        {
            ServiceDescriptor descriptor = Assert.Single(
                services,
                candidate => candidate.ServiceType == port);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
            Assert.NotNull(descriptor.ImplementationFactory);
        }
    }
}
