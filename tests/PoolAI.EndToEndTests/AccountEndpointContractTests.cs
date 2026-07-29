#pragma warning disable MA0051 // HTTP contract scenarios keep their complete request protocol visible.
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application;

namespace PoolAI.EndToEndTests;

public sealed class AccountEndpointContractTests
{
    private const string CreateCredential = "account-create-secret-0001";
    private const string UpdateCredential = "account-update-secret-0002";
    private static readonly EntityId ActorId = new(Guid.Parse(
        "019bd5e8-30e0-7d4c-a7f2-bb1db0634180"));
    private static readonly EntityId AccountId = new(Guid.Parse(
        "019bd5e8-30e0-7d4c-a7f2-bb1db0634181"));
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse(
        "2026-07-30T10:00:00Z",
        CultureInfo.InvariantCulture);

    [Fact]
    public async Task AccountCredentialsAreNeverReturnedOrLogged()
    {
        await using AccountApiFactory factory = new();
        using HttpClient admin = AuthenticatedClient(factory, "admin");
        factory.UseCases.CreateResult = Result.Success(new AccountCommandOutcome<AccountView>(
            StatusCodes.Status201Created,
            IsReplay: false,
            View(version: 1, prefix: "sha256:111111111111"),
            "\"v1\""));

        using HttpRequestMessage create = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/accounts",
            new
            {
                name = "Primary",
                provider = "openai_compatible",
                base_url = "https://EXAMPLE.com/v1",
                credential = CreateCredential,
                max_concurrency = 4,
                priority = 2,
                weight = 100,
            },
            idempotencyKey: "account-create");
        create.Headers.TryAddWithoutValidation("User-Agent", "account-test-client");
        using HttpResponseMessage created = await admin.SendAsync(
            create,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("\"v1\"", created.Headers.ETag?.Tag);
        Assert.Equal(
            $"/api/v1/admin/accounts/{AccountId.Value:D}",
            created.Headers.Location?.OriginalString);
        string createdBody = await created.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        AssertSecretFree(createdBody, CreateCredential, UpdateCredential);
        AssertAccountShape(createdBody, "sha256:111111111111", version: 1);
        CreateAccountCommand createCommand = Assert.IsType<CreateAccountCommand>(
            factory.UseCases.LastCreate);
        Assert.Equal(CreateCredential, createCommand.Credential);
        Assert.Equal(nameof(CreateAccountCommand), createCommand.ToString());
        Assert.Equal(UpstreamProvider.OpenAiCompatible, createCommand.Provider);
        Assert.NotEqual("account-test-client", createCommand.UserAgent);

        factory.UseCases.GetResult = Result.Success(
            View(version: 2, prefix: "sha256:222222222222"));
        using HttpClient auditor = AuthenticatedClient(factory, "auditor");
        using HttpResponseMessage get = await auditor.GetAsync(
            $"/api/v1/admin/accounts/{AccountId.Value:D}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("\"v2\"", get.Headers.ETag?.Tag);
        string getBody = await get.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        AssertSecretFree(getBody, CreateCredential, UpdateCredential);
        AssertAccountShape(getBody, "sha256:222222222222", version: 2);

        factory.UseCases.ListResult = Result.Success(new AccountPage(
            [View(version: 3, prefix: "sha256:333333333333")],
            NextCursor: "next-account",
            HasMore: true));
        using HttpResponseMessage list = await auditor.GetAsync(
            "/api/v1/admin/accounts?cursor=previous-account&limit=25",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        string listBody = await list.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        AssertSecretFree(listBody, CreateCredential, UpdateCredential);
        using (JsonDocument document = JsonDocument.Parse(listBody))
        {
            JsonElement data = Assert.Single(
                document.RootElement.GetProperty("data").EnumerateArray());
            Assert.Equal("sha256:333333333333",
                data.GetProperty("credential_prefix").GetString());
            Assert.Equal("next-account",
                document.RootElement.GetProperty("page")
                    .GetProperty("next_cursor").GetString());
        }

        factory.UseCases.UpdateResult = Result.Success(
            new AccountCommandOutcome<AccountView>(
                StatusCodes.Status200OK,
                IsReplay: false,
                View(version: 4, prefix: "sha256:444444444444"),
                "\"v4\""));
        using HttpClient updateAdmin = AuthenticatedClient(factory, "admin");
        using HttpRequestMessage update = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/accounts/{AccountId.Value:D}",
            new
            {
                credential = UpdateCredential,
                reason = "scheduled rotation",
            },
            "application/merge-patch+json",
            "account-update",
            "\"v3\"");
        using HttpResponseMessage updated = await updateAdmin.SendAsync(
            update,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("\"v4\"", updated.Headers.ETag?.Tag);
        string updatedBody = await updated.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        AssertSecretFree(updatedBody, CreateCredential, UpdateCredential);
        AssertAccountShape(updatedBody, "sha256:444444444444", version: 4);
        UpdateAccountCommand updateCommand = Assert.IsType<UpdateAccountCommand>(
            factory.UseCases.LastUpdate);
        Assert.Equal(UpdateCredential, updateCommand.Credential);
        Assert.Equal(nameof(UpdateAccountCommand), updateCommand.ToString());
        Assert.Equal("scheduled rotation", updateCommand.Reason);

        factory.UseCases.UpdateResult =
            Result.Failure<AccountCommandOutcome<AccountView>>(
                AccountErrorCodes.ResourceConflict,
                $"synthetic failure containing {UpdateCredential}");
        using HttpRequestMessage failedUpdate = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/accounts/{AccountId.Value:D}",
            new { name = "Still Safe" },
            "application/merge-patch+json",
            "account-safe-problem",
            "\"v4\"");
        using HttpResponseMessage problem = await updateAdmin.SendAsync(
            failedUpdate,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, problem.StatusCode);
        string problemBody = await problem.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        AssertSecretFree(problemBody, CreateCredential, UpdateCredential);
        Assert.Contains("resource_conflict", problemBody, StringComparison.Ordinal);

        string logs = string.Join('\n', factory.Logs.Messages);
        AssertSecretFree(logs, CreateCredential, UpdateCredential);
    }

    [Fact]
    public async Task AccountTransportRequiresFrozenMediaTypesAndConcurrencyHeaders()
    {
        await using AccountApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "operator");

        using HttpRequestMessage wrongCreateContent = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/accounts",
            ValidCreateBody(),
            "application/problem+json",
            "wrong-content");
        using HttpResponseMessage wrongCreate = await client.SendAsync(
            wrongCreateContent,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            wrongCreate,
            HttpStatusCode.UnsupportedMediaType,
            "unsupported_media_type");

        using HttpRequestMessage missingCreateKey = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/accounts",
            ValidCreateBody());
        using HttpResponseMessage missingKey = await client.SendAsync(
            missingCreateKey,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            missingKey,
            HttpStatusCode.PreconditionRequired,
            "idempotency_key_required");

        using HttpRequestMessage wrongPatchContent = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/accounts/{AccountId.Value:D}",
            new { name = "Renamed" },
            "application/json",
            "wrong-patch-content",
            "\"v1\"");
        using HttpResponseMessage wrongPatch = await client.SendAsync(
            wrongPatchContent,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            wrongPatch,
            HttpStatusCode.UnsupportedMediaType,
            "unsupported_media_type");

        using HttpRequestMessage missingPatchKey = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/accounts/{AccountId.Value:D}",
            new { name = "Renamed" },
            "application/merge-patch+json",
            ifMatch: "\"v1\"");
        using HttpResponseMessage patchKey = await client.SendAsync(
            missingPatchKey,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            patchKey,
            HttpStatusCode.PreconditionRequired,
            "idempotency_key_required");

        using HttpRequestMessage missingIfMatch = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/accounts/{AccountId.Value:D}",
            new { name = "Renamed" },
            "application/merge-patch+json",
            "missing-if-match");
        using HttpResponseMessage precondition = await client.SendAsync(
            missingIfMatch,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            precondition,
            HttpStatusCode.PreconditionRequired,
            "if_match_required");

        using HttpRequestMessage weakIfMatch = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/accounts/{AccountId.Value:D}",
            new { name = "Renamed" },
            "application/merge-patch+json",
            "weak-if-match",
            "W/\"v1\"");
        using HttpResponseMessage weak = await client.SendAsync(
            weakIfMatch,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            weak,
            HttpStatusCode.BadRequest,
            "invalid_request",
            "/headers/If-Match");

        using HttpRequestMessage missingReason = new(
            HttpMethod.Delete,
            $"/api/v1/admin/accounts/{AccountId.Value:D}");
        missingReason.Headers.TryAddWithoutValidation(
            "Idempotency-Key",
            "retire-missing-reason");
        missingReason.Headers.TryAddWithoutValidation("If-Match", "\"v4\"");
        using HttpResponseMessage noReason = await client.SendAsync(
            missingReason,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            noReason,
            HttpStatusCode.BadRequest,
            "invalid_request",
            "/headers/X-Change-Reason");

        factory.UseCases.RetireResult = Result.Success(new AccountCommandOutcome(
            StatusCodes.Status204NoContent,
            IsReplay: false,
            "\"v5\""));
        using HttpRequestMessage retire = new(
            HttpMethod.Delete,
            $"/api/v1/admin/accounts/{AccountId.Value:D}");
        retire.Headers.TryAddWithoutValidation("Idempotency-Key", "account-retire");
        retire.Headers.TryAddWithoutValidation("If-Match", "\"v4\"");
        retire.Headers.TryAddWithoutValidation(
            "X-Change-Reason",
            "upstream decommissioned");
        using HttpResponseMessage retired = await client.SendAsync(
            retire,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, retired.StatusCode);
        Assert.Equal("\"v5\"", retired.Headers.ETag?.Tag);
        RetireAccountCommand retireCommand = Assert.IsType<RetireAccountCommand>(
            factory.UseCases.LastRetire);
        Assert.Equal(4, retireCommand.ExpectedVersion);
        Assert.Equal("account-retire", retireCommand.IdempotencyKey);
        Assert.Equal("upstream decommissioned", retireCommand.Reason);
    }

    [Fact]
    public async Task AccountRbacAllowsAuditedReadsAndAdminOperatorWritesOnly()
    {
        await using AccountApiFactory factory = new();
        foreach (string role in new[] { "admin", "operator", "auditor" })
        {
            using HttpClient reader = AuthenticatedClient(factory, role);
            using HttpResponseMessage response = await reader.GetAsync(
                $"/api/v1/admin/accounts/{AccountId.Value:D}",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using HttpClient auditor = AuthenticatedClient(factory, "auditor");
        using HttpRequestMessage auditorWrite = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/accounts",
            ValidCreateBody(),
            idempotencyKey: "auditor-forbidden");
        using HttpResponseMessage forbiddenWrite = await auditor.SendAsync(
            auditorWrite,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            forbiddenWrite,
            HttpStatusCode.Forbidden,
            "role_required");

        using HttpClient user = AuthenticatedClient(factory, "user");
        using HttpResponseMessage forbiddenRead = await user.GetAsync(
            "/api/v1/admin/accounts",
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            forbiddenRead,
            HttpStatusCode.Forbidden,
            "role_required");

        using HttpClient anonymous = factory.CreateClient();
        using HttpResponseMessage unauthorized = await anonymous.GetAsync(
            "/api/v1/admin/accounts",
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            unauthorized,
            HttpStatusCode.Unauthorized,
            "authentication_required");
        Assert.Contains(
            unauthorized.Headers.WwwAuthenticate,
            static value => string.Equals(
                value.Scheme,
                "Bearer",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidBaseUrlReturnsSafeValidationProblemBeforeUseCase()
    {
        await using AccountApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");
        const string submittedUrl = "https://bad-.example/private-marker";
        const string submittedCredential = "invalid-url-secret-0003";
        using HttpRequestMessage request = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/accounts",
            new
            {
                name = "Invalid URL",
                provider = "openai",
                base_url = submittedUrl,
                credential = submittedCredential,
                max_concurrency = 1,
            },
            idempotencyKey: "invalid-base-url");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        await AssertProblemAsync(
            response,
            HttpStatusCode.UnprocessableEntity,
            "validation_failed",
            "/base_url");
        string body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        AssertSecretFree(body, submittedUrl, submittedCredential, "private-marker");
        Assert.Equal(0, factory.UseCases.CreateCalls);
    }

    private static object ValidCreateBody() => new
    {
        name = "Primary",
        provider = "openai",
        base_url = "https://api.example.com/v1",
        credential = CreateCredential,
        max_concurrency = 4,
    };

    private static AccountView View(long version, string prefix) => new(
        AccountId,
        "Primary",
        UpstreamProvider.OpenAiCompatible,
        new Uri("https://EXAMPLE.com/v1", UriKind.Absolute),
        prefix,
        AccountLifecycle.Disabled,
        new AccountHealthView(
            AccountHealth.Unknown,
            RetryAt: null,
            LastCheckedAt: null),
        ActiveLeases: 0,
        MaxConcurrency: 4,
        Priority: 2,
        Weight: 100,
        version,
        Timestamp,
        Timestamp.AddMinutes(version));

    private static void AssertAccountShape(
        string json,
        string prefix,
        long version)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement account = document.RootElement;
        Assert.Equal(AccountId.Value, account.GetProperty("id").GetGuid());
        Assert.Equal("openai", account.GetProperty("platform").GetString());
        Assert.Equal("openai_compatible", account.GetProperty("provider").GetString());
        Assert.Equal("api_key", account.GetProperty("account_type").GetString());
        Assert.Equal(prefix, account.GetProperty("credential_prefix").GetString());
        Assert.Equal(version, account.GetProperty("version").GetInt64());
        Assert.Equal(0, account.GetProperty("active_leases").GetInt64());
    }

    private static void AssertSecretFree(string value, params string[] forbidden)
    {
        foreach (string text in forbidden)
        {
            Assert.DoesNotContain(text, value, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("\"credential\":", value, StringComparison.Ordinal);
        Assert.DoesNotContain("credential_envelope", value, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kid\":", value, StringComparison.Ordinal);
        Assert.DoesNotContain("key_version", value, StringComparison.Ordinal);
    }

    private static HttpRequestMessage JsonCommand(
        HttpMethod method,
        string path,
        object body,
        string contentType = "application/json",
        string? idempotencyKey = null,
        string? ifMatch = null)
    {
        HttpRequestMessage request = new(method, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return request;
    }

    private static HttpClient AuthenticatedClient(AccountApiFactory factory, string role)
    {
        factory.AccessSessionValidator.CanonicalRole = role switch
        {
            "admin" => SystemRole.Admin,
            "operator" => SystemRole.Operator,
            "auditor" => SystemRole.Auditor,
            "user" => SystemRole.User,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            IdentityAuthorizationTests.CreateJwt(
                factory.JwtSigningKey,
                "PoolAI",
                "PoolAI.Web",
                role,
                tokenVersion: 7,
                TimeProvider.System.GetUtcNow().AddMinutes(5),
                subjectId: ActorId.Value));
        return client;
    }

    private static async ValueTask AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        string? pointer = null)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken).ConfigureAwait(false));
        JsonElement problem = document.RootElement;
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(problem.GetProperty("retryable").GetBoolean());
        Assert.True(Guid.TryParse(
            problem.GetProperty("request_id").GetString(),
            out _));
        if (pointer is not null)
        {
            Assert.True(problem.GetProperty("errors").TryGetProperty(
                pointer,
                out JsonElement messages));
            Assert.NotEmpty(messages.EnumerateArray());
        }
    }

    private sealed class AccountApiFactory : PoolAiApiFactory
    {
        internal FakeAccountUseCases UseCases { get; } = new();

        internal RecordingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
            builder.ConfigureServices(services =>
            {
                for (int index = services.Count - 1; index >= 0; index--)
                {
                    if (services[index].ServiceType.FullName is
                        "PoolAI.Modules.Supply.Application.AccountControlPlaneService")
                    {
                        services.RemoveAt(index);
                    }
                }

                services.RemoveAll<IListAccountsUseCase>();
                services.RemoveAll<IGetAccountUseCase>();
                services.RemoveAll<ICreateAccountUseCase>();
                services.RemoveAll<IUpdateAccountUseCase>();
                services.RemoveAll<IRetireAccountUseCase>();
                services.AddSingleton<IListAccountsUseCase>(UseCases);
                services.AddSingleton<IGetAccountUseCase>(UseCases);
                services.AddSingleton<ICreateAccountUseCase>(UseCases);
                services.AddSingleton<IUpdateAccountUseCase>(UseCases);
                services.AddSingleton<IRetireAccountUseCase>(UseCases);
            });
        }
    }

    private sealed class FakeAccountUseCases :
        IListAccountsUseCase,
        IGetAccountUseCase,
        ICreateAccountUseCase,
        IUpdateAccountUseCase,
        IRetireAccountUseCase
    {
        internal Result<AccountPage> ListResult { get; set; } = Result.Success(
            new AccountPage(
                [View(version: 1, prefix: "sha256:aaaaaaaaaaaa")],
                null,
                HasMore: false));

        internal Result<AccountView> GetResult { get; set; } = Result.Success(
            View(version: 1, prefix: "sha256:aaaaaaaaaaaa"));

        internal Result<AccountCommandOutcome<AccountView>> CreateResult { get; set; } =
            Result.Success(new AccountCommandOutcome<AccountView>(
                StatusCodes.Status201Created,
                IsReplay: false,
                View(version: 1, prefix: "sha256:aaaaaaaaaaaa"),
                "\"v1\""));

        internal Result<AccountCommandOutcome<AccountView>> UpdateResult { get; set; } =
            Result.Success(new AccountCommandOutcome<AccountView>(
                StatusCodes.Status200OK,
                IsReplay: false,
                View(version: 2, prefix: "sha256:bbbbbbbbbbbb"),
                "\"v2\""));

        internal Result<AccountCommandOutcome> RetireResult { get; set; } =
            Result.Success(new AccountCommandOutcome(
                StatusCodes.Status204NoContent,
                IsReplay: false,
                "\"v2\""));

        internal CreateAccountCommand? LastCreate { get; private set; }

        internal UpdateAccountCommand? LastUpdate { get; private set; }

        internal RetireAccountCommand? LastRetire { get; private set; }

        internal int CreateCalls { get; private set; }

        public ValueTask<Result<AccountPage>> ExecuteAsync(
            ListAccountsQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ListResult);
        }

        public ValueTask<Result<AccountView>> ExecuteAsync(
            GetAccountQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(GetResult);
        }

        public ValueTask<Result<AccountCommandOutcome<AccountView>>> ExecuteAsync(
            CreateAccountCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            LastCreate = command;
            return ValueTask.FromResult(CreateResult);
        }

        public ValueTask<Result<AccountCommandOutcome<AccountView>>> ExecuteAsync(
            UpdateAccountCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastUpdate = command;
            return ValueTask.FromResult(UpdateResult);
        }

        public ValueTask<Result<AccountCommandOutcome>> ExecuteAsync(
            RetireAccountCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRetire = command;
            return ValueTask.FromResult(RetireResult);
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        internal ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(Messages);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, exception));
                if (exception is not null)
                {
                    messages.Enqueue(exception.GetType().Name);
                }
            }
        }
    }
}
#pragma warning restore MA0051
