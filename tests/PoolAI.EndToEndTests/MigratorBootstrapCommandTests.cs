using PoolAI.Migrator;

namespace PoolAI.EndToEndTests;

public sealed class MigratorBootstrapCommandTests
{
    [Fact]
    public void NoCommandAppliesMigrationsOnly()
    {
        MigratorInvocation invocation = MigratorCommandParser.Parse([]);

        Assert.False(invocation.ShouldBootstrapAdmin);
        Assert.Null(invocation.Email);
        Assert.Null(invocation.DisplayName);
    }

    [Fact]
    public void BootstrapCommandAcceptsOnlyTheTwoNamedNonSecretOptions()
    {
        MigratorInvocation invocation = MigratorCommandParser.Parse(
        [
            "bootstrap-admin",
            "--display-name",
            "Initial Administrator",
            "--email",
            "admin@example.test",
            "--password-stdin",
        ]);

        Assert.True(invocation.ShouldBootstrapAdmin);
        Assert.Equal("admin@example.test", invocation.Email);
        Assert.Equal("Initial Administrator", invocation.DisplayName);
        Assert.DoesNotContain("admin@example.test", invocation.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--password")]
    [InlineData("--bootstrap-token")]
    [InlineData("--token")]
    public void SecretCommandLineOptionsAreRejectedWithoutRenderingTheirValue(string option)
    {
        const string Secret = "must-not-appear-in-errors";
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            MigratorCommandParser.Parse(
            [
                "bootstrap-admin",
                "--email",
                "admin@example.test",
                option,
                Secret,
                "--password-stdin",
            ]));

        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void BootstrapCommandRequiresExplicitStandardInputFlag()
    {
        _ = Assert.Throws<InvalidOperationException>(() => MigratorCommandParser.Parse(
        [
            "bootstrap-admin",
            "--email",
            "admin@example.test",
            "--display-name",
            "Initial Administrator",
        ]));
    }

    [Fact]
    public async Task SecretsAreReadFromTwoStandardInputLinesAndRenderRedacted()
    {
        const string Password = "bootstrap-password-42";
        const string Token = "bootstrap-token-0123456789abcdef-42";
        using StringReader input = new($"{Password}\n{Token}\n");

        PoolAI.Database.Migrations.AdminBootstrapSecrets secrets = await BootstrapAdminSecretReader
            .ReadAsync(input, TestContext.Current.CancellationToken);

        Assert.Equal(Password, secrets.Password);
        Assert.Equal(Token, secrets.BootstrapToken);
        Assert.Equal("[REDACTED]", secrets.ToString());
        Assert.DoesNotContain(Password, secrets.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, secrets.ToString(), StringComparison.Ordinal);
        PoolAI.Database.Migrations.AdminBootstrapRequest request = new(
            "admin@example.test",
            "Initial Administrator",
            secrets);
        Assert.Equal("[Admin bootstrap request]", request.ToString());
        Assert.DoesNotContain(Password, request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, request.ToString(), StringComparison.Ordinal);
        Assert.Null(input.ReadLine());
    }

    [Fact]
    public async Task MissingInputFailsWithoutRenderingThePartialSecret()
    {
        const string PartialSecret = "bootstrap-password-42";
        using StringReader input = new(PartialSecret);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BootstrapAdminSecretReader
                .ReadAsync(input, TestContext.Current.CancellationToken)
                .AsTask());

        Assert.DoesNotContain(PartialSecret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdditionalInputFailsWithoutRenderingAnySecret()
    {
        const string Password = "bootstrap-password-42";
        const string Token = "bootstrap-token-0123456789abcdef-42";
        const string Extra = "unexpected-third-line";
        using StringReader input = new($"{Password}\n{Token}\n{Extra}\n");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BootstrapAdminSecretReader
                .ReadAsync(input, TestContext.Current.CancellationToken)
                .AsTask());

        Assert.DoesNotContain(Password, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Extra, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void BootstrapInputBoundsMatchTheIdentityContract()
    {
        PoolAI.Database.Migrations.AdminBootstrapSecrets maximumSecrets = new(
            new string('p', 1_024),
            new string('t', 512));
        PoolAI.Database.Migrations.AdminBootstrapRequest maximumRequest = new(
            "admin@example.test",
            new string('d', 100),
            maximumSecrets);

        Assert.Equal(100, maximumRequest.DisplayName.Length);
        Assert.Equal(1_024, maximumRequest.Secrets.Password.Length);
        Assert.Equal(512, maximumRequest.Secrets.BootstrapToken.Length);
        _ = Assert.Throws<ArgumentException>(() =>
            new PoolAI.Database.Migrations.AdminBootstrapRequest(
                "admin@example.test",
                new string('d', 101),
                maximumSecrets));
        _ = Assert.Throws<ArgumentException>(() =>
            new PoolAI.Database.Migrations.AdminBootstrapRequest(
                "admin@example.test",
                "Initial\0Administrator",
                maximumSecrets));
        _ = Assert.Throws<ArgumentException>(() =>
            new PoolAI.Database.Migrations.AdminBootstrapSecrets(
                new string('p', 1_025),
                new string('t', 32)));
        _ = Assert.Throws<ArgumentException>(() =>
            new PoolAI.Database.Migrations.AdminBootstrapSecrets(
                "bootstrap\0password-42",
                new string('t', 32)));
        _ = Assert.Throws<ArgumentException>(() =>
            new PoolAI.Database.Migrations.AdminBootstrapSecrets(
                "bootstrap-password-42",
                $"{new string('t', 31)}\0"));
    }

    [Fact]
    public void BootstrapMailboxUsesTheSameCanonicalIdnaShapeAsEmailDelivery()
    {
        PoolAI.Database.Migrations.AdminBootstrapRequest request = new(
            "Admin+Bootstrap@BÜCHER.Example",
            "Initial Administrator",
            new PoolAI.Database.Migrations.AdminBootstrapSecrets(
                "bootstrap-password-42",
                "bootstrap-token-0123456789abcdef-42"));

        Assert.Equal("Admin+Bootstrap@xn--bcher-kva.example", request.Email);
        Assert.Equal("admin+bootstrap@xn--bcher-kva.example", request.NormalizedEmail);
    }

    [Theory]
    [InlineData("PoolAI <admin@example.test>")]
    [InlineData(" admin@example.test")]
    [InlineData("admin@example.test ")]
    [InlineData("\"admin\"@example.test")]
    [InlineData("admín@example.test")]
    [InlineData("admin..bootstrap@example.test")]
    [InlineData("admin@[127.0.0.1]")]
    [InlineData("admin@invalid_domain.test")]
    public void BootstrapMailboxRejectsValuesTheEmailWorkerCannotDeliver(string email)
    {
        _ = Assert.Throws<ArgumentException>(() =>
            new PoolAI.Database.Migrations.AdminBootstrapRequest(
                email,
                "Initial Administrator",
                new PoolAI.Database.Migrations.AdminBootstrapSecrets(
                    "bootstrap-password-42",
                    "bootstrap-token-0123456789abcdef-42")));
    }
}
