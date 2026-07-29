using PoolAI.BuildingBlocks;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Domain;

namespace PoolAI.UnitTests;

public sealed class ChannelAndGroupSupplyDomainTests
{
    [Fact]
    public void ChannelInputsNormalizeAndSortTheAggregateValues()
    {
        Assert.Equal("primary", ChannelInput.Name("  primary  "));
        Assert.Equal("because", ChannelInput.Reason(" because "));

        IReadOnlyList<ChannelModelMappingValue> mappings =
            ChannelInput.ModelMappings(
            [
                new("z-model", "upstream-z"),
                new(" a-model ", " upstream-a "),
            ]);

        Assert.Collection(
            mappings,
            first =>
            {
                Assert.Equal("a-model", first.ClientModel);
                Assert.Equal("upstream-a", first.UpstreamModel);
            },
            second => Assert.Equal("z-model", second.ClientModel));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line\nbreak")]
    public void InvalidChannelNamesAreRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => ChannelInput.Name(value));
    }

    [Fact]
    public void InvalidUnicodeAndOversizedChannelTextAreRejected()
    {
        Assert.Throws<ArgumentException>(
            () => ChannelInput.Name(new string('x', 101)));
        Assert.Throws<ArgumentException>(
            () => ChannelInput.Name(new string('\ud800', 1)));
        Assert.Throws<ArgumentException>(
            () => ChannelInput.ModelMappings(
            [
                new ChannelModelMappingValue(
                    new string('m', 201),
                    "upstream"),
            ]));
    }

    [Fact]
    public void EmptyAndDuplicateChannelMappingsAreRejected()
    {
        Assert.Throws<ArgumentException>(
            () => ChannelInput.ModelMappings([]));
        Assert.Throws<ArgumentException>(
            () => ChannelInput.ModelMappings(
            [
                new("same", "upstream-a"),
                new("same", "upstream-b"),
            ]));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r ")]
    public void InvalidChannelReasonsAreRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => ChannelInput.Reason(value));
    }

    [Fact]
    public void ChannelVersionAndLifecycleFencesAreStrict()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ChannelInput.ExpectedVersion(0));
        ChannelResource retired = Channel(
            ChannelResourceStatus.Retired,
            [new("client", "upstream")]);
        Assert.Throws<InvalidOperationException>(
            () => ChannelInput.ValidateMutation(retired, null, null));
        Assert.Throws<InvalidOperationException>(
            () => ChannelInput.ValidateRetirement(retired));

        ChannelResource disabled = Channel(
            ChannelResourceStatus.Disabled,
            []);
        Assert.Throws<InvalidOperationException>(() =>
            ChannelInput.ValidateMutation(
                disabled,
                ChannelResourceStatus.Active,
                requestedMappings: null));
        Assert.Throws<InvalidOperationException>(() =>
            ChannelInput.ValidateMutation(
                Channel(ChannelResourceStatus.Active, [new("a", "b")]),
                ChannelResourceStatus.Retired,
                requestedMappings: null));
        ChannelInput.ValidateRetirement(disabled);
    }

    [Fact]
    public void GroupSupplyBindingsAllowEmptyAndCanonicalizeByAccountId()
    {
        Assert.Empty(GroupSupplyInput.Bindings([]));
        EntityId later = new(Guid.Parse("ffffffff-ffff-7fff-bfff-ffffffffffff"));
        EntityId earlier = new(Guid.Parse("00000000-0000-7000-8000-000000000001"));
        IReadOnlyList<GroupSupplyBindingValue> bindings =
            GroupSupplyInput.Bindings(
            [
                new(later, true, 1, 2),
                new(earlier, false, null, null),
            ]);

        Assert.Equal(earlier, bindings[0].AccountId);
        Assert.False(bindings[0].Enabled);
        Assert.Equal(later, bindings[1].AccountId);
    }

    [Fact]
    public void GroupSupplyBindingsRejectDuplicateAndOutOfRangeOverrides()
    {
        EntityId accountId = EntityId.New();
        Assert.Throws<ArgumentException>(() => GroupSupplyInput.Bindings(
        [
            new(accountId, true, null, null),
            new(accountId, false, null, null),
        ]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GroupSupplyInput.Bindings(
            [
                new(EntityId.New(), true, -100001, null),
            ]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GroupSupplyInput.Bindings(
            [
                new(EntityId.New(), true, null, 0),
            ]));
    }

    [Fact]
    public void GroupSupplyVersionAndReasonValidationAreStrict()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GroupSupplyInput.ExpectedVersion(-1));
        Assert.Throws<ArgumentException>(
            () => GroupSupplyInput.Reason(" "));
        Assert.Throws<ArgumentException>(
            () => GroupSupplyInput.Reason("line\nbreak"));
        Assert.Equal("change", GroupSupplyInput.Reason(" change "));
    }

    private static ChannelResource Channel(
        ChannelResourceStatus status,
        IReadOnlyList<ChannelModelMappingValue> mappings) => new(
        EntityId.New(),
        UpstreamProvider.OpenAi,
        "channel",
        status,
        new ChannelCapabilitiesValue(true, true, true, true),
        mappings,
        1,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch);
}
