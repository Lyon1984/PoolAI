#pragma warning disable MA0051 // Route adapters keep the frozen request mapping visible.
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PoolAI.BuildingBlocks;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.Supply.Application;
using PoolAI.Modules.Supply.Domain;
using ContractAccountHealth = PoolAI.Contracts.Generated.AccountHealth;
using ContractAccountHealthStatus = PoolAI.Contracts.Generated.AccountHealthStatus;
using ContractAccountStatus = PoolAI.Contracts.Generated.AccountStatus;
using ContractProvider = PoolAI.Contracts.Generated.UpstreamProvider;
using SupplyAccountHealth = PoolAI.Modules.Supply.Abstractions.AccountHealth;
using SupplyProvider = PoolAI.Modules.Supply.Abstractions.UpstreamProvider;

namespace PoolAI.Modules.Supply.Endpoints;

internal static class SupplyAccountEndpointMappings
{
    internal static IEndpointRouteBuilder MapSupplyAccountEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder accounts = endpoints.MapGroup("/api/v1/admin/accounts");
        accounts.AddEndpointFilter(static async (invocation, next) =>
        {
            HttpContext context = invocation.HttpContext;
            return SupplyHttp.TryGetActor(context, out _)
                ? await next(invocation).ConfigureAwait(false)
                : SupplyHttp.InvalidUserToken(context);
        });
        accounts.MapGet("/", ListAsync)
            .RequireAuthorization(RequireReadRole)
            .WithName("adminListAccounts");
        accounts.MapPost("/", CreateAsync)
            .RequireAuthorization(RequireWriteRole)
            .WithName("adminCreateAccount");
        accounts.MapGet("/{accountId:guid}", GetAsync)
            .RequireAuthorization(RequireReadRole)
            .WithName("adminGetAccount");
        accounts.MapMethods("/{accountId:guid}", [HttpMethods.Patch], UpdateAsync)
            .RequireAuthorization(RequireWriteRole)
            .WithName("adminUpdateAccount");
        accounts.MapDelete("/{accountId:guid}", RetireAsync)
            .RequireAuthorization(RequireWriteRole)
            .WithName("adminRetireAccount");
        return endpoints;
    }

    private static void RequireReadRole(AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser().RequireRole("admin", "operator", "auditor");

    private static void RequireWriteRole(AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser().RequireRole("admin", "operator");

    private static async Task<IResult> ListAsync(
        HttpContext context,
        IListAccountsUseCase useCase,
        string? cursor,
        string? limit)
    {
        if (!SupplyHttp.TryGetPagination(context, limit, out int parsedLimit, out IResult? failure))
        {
            return failure!;
        }

        Result<PoolAI.Modules.Supply.Application.AccountPage> result =
            await useCase.ExecuteAsync(
                new ListAccountsQuery(ToAccountActor(context), cursor, parsedLimit),
                context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return SupplyHttp.FromError(context, result.Error);
        }

        PoolAI.Modules.Supply.Application.AccountPage page = result.Value;
        return Results.Ok(new PoolAI.Contracts.Generated.AccountPage
        {
            Data = page.Data.Select(ToContract).ToArray(),
            Page = new PageInfo
            {
                HasMore = page.HasMore,
                NextCursor = page.NextCursor is null ? default : page.NextCursor,
            },
        });
    }

    private static async Task<IResult> GetAsync(
        HttpContext context,
        IGetAccountUseCase useCase,
        Guid accountId)
    {
        if (!SupplyHttp.TryGetEntityId(
                context,
                accountId,
                "/accountId",
                "Account",
                out EntityId id,
                out IResult? failure))
        {
            return failure!;
        }

        Result<AccountView> result = await useCase.ExecuteAsync(
            new GetAccountQuery(ToAccountActor(context), id),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return SupplyHttp.FromError(context, result.Error);
        }

        context.Response.Headers.ETag = SupplyHttp.ETag(result.Value.Version);
        return Results.Ok(ToContract(result.Value));
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        ICreateAccountUseCase useCase,
        AccountCreateWireRequest request)
    {
        IResult? failure = SupplyHttp.RequireContentType(context, "application/json");
        if (failure is not null)
        {
            return failure;
        }

        Dictionary<string, IReadOnlyList<string>> errors = Validate(request);
        if (errors.Count != 0)
        {
            return SupplyHttp.ValidationProblem(context, errors);
        }

        if (!SupplyHttp.TryGetIdempotencyKey(context, out string? key, out failure))
        {
            return failure!;
        }

        Result<AccountCommandOutcome<AccountView>> result = await useCase.ExecuteAsync(
            new CreateAccountCommand(
                SupplyHttp.RequestId(context),
                ToAccountActor(context),
                key!,
                request.Name,
                ToProvider(request.Provider),
                request.BaseUrl,
                request.Credential,
                checked((int)request.MaxConcurrency),
                checked((int)(request.Priority.HasValue ? request.Priority.Value : 0)),
                checked((int)(request.Weight.HasValue ? request.Weight.Value : 100)),
                SupplyHttp.RemoteIp(context),
                SupplyHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return SupplyHttp.FromError(context, result.Error);
        }

        AccountCommandOutcome<AccountView> outcome = result.Value;
        context.Response.Headers.ETag = outcome.ETag;
        context.Response.Headers.Location = outcome.Location
            ?? $"/api/v1/admin/accounts/{outcome.Value.Id.Value:D}";
        return Results.Json(
            ToContract(outcome.Value),
            statusCode: outcome.StatusCode);
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext context,
        IUpdateAccountUseCase useCase,
        Guid accountId,
        AccountUpdateWireRequest request)
    {
        IResult? failure = SupplyHttp.RequireContentType(
            context,
            "application/merge-patch+json");
        if (failure is not null)
        {
            return failure;
        }

        if (!SupplyHttp.TryGetEntityId(
                context,
                accountId,
                "/accountId",
                "Account",
                out EntityId id,
                out failure))
        {
            return failure!;
        }

        Dictionary<string, IReadOnlyList<string>> errors = Validate(request);
        if (errors.Count != 0)
        {
            return SupplyHttp.ValidationProblem(context, errors);
        }

        if (!SupplyHttp.TryGetIdempotencyKey(context, out string? key, out failure)
            || !SupplyHttp.TryGetExpectedVersion(
                context,
                out long expectedVersion,
                out failure))
        {
            return failure!;
        }

        Result<AccountCommandOutcome<AccountView>> result = await useCase.ExecuteAsync(
            new UpdateAccountCommand(
                SupplyHttp.RequestId(context),
                ToAccountActor(context),
                key!,
                id,
                expectedVersion,
                request.Name.HasValue,
                request.Name.HasValue ? request.Name.Value : null,
                request.BaseUrl.HasValue,
                request.BaseUrl.HasValue ? request.BaseUrl.Value : null,
                request.Credential.HasValue,
                request.Credential.HasValue ? request.Credential.Value : null,
                request.Status.HasValue,
                request.Status.HasValue ? ToLifecycle(request.Status.Value!) : null,
                request.MaxConcurrency.HasValue,
                request.MaxConcurrency.HasValue
                    ? checked((int)request.MaxConcurrency.Value)
                    : null,
                request.Priority.HasValue,
                request.Priority.HasValue ? checked((int)request.Priority.Value) : null,
                request.Weight.HasValue,
                request.Weight.HasValue ? checked((int)request.Weight.Value) : null,
                request.Reason.HasValue ? request.Reason.Value : null,
                SupplyHttp.RemoteIp(context),
                SupplyHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return SupplyHttp.FromError(context, result.Error);
        }

        AccountCommandOutcome<AccountView> outcome = result.Value;
        context.Response.Headers.ETag = outcome.ETag;
        return Results.Ok(ToContract(outcome.Value));
    }

    private static async Task<IResult> RetireAsync(
        HttpContext context,
        IRetireAccountUseCase useCase,
        Guid accountId)
    {
        if (!SupplyHttp.TryGetEntityId(
                context,
                accountId,
                "/accountId",
                "Account",
                out EntityId id,
                out IResult? failure)
            || !SupplyHttp.TryGetIdempotencyKey(
                context,
                out string? key,
                out failure)
            || !SupplyHttp.TryGetExpectedVersion(
                context,
                out long expectedVersion,
                out failure)
            || !SupplyHttp.TryGetChangeReason(
                context,
                out string? reason,
                out failure))
        {
            return failure!;
        }

        Result<AccountCommandOutcome> result = await useCase.ExecuteAsync(
            new RetireAccountCommand(
                SupplyHttp.RequestId(context),
                ToAccountActor(context),
                key!,
                id,
                expectedVersion,
                reason!,
                SupplyHttp.RemoteIp(context),
                SupplyHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return SupplyHttp.FromError(context, result.Error);
        }

        context.Response.Headers.ETag = result.Value.ETag;
        return Results.NoContent();
    }

    private static Dictionary<string, IReadOnlyList<string>> Validate(
        AccountCreateWireRequest request)
    {
        Dictionary<string, IReadOnlyList<string>> errors = new(StringComparer.Ordinal);
        ValidateName(request.Name, errors);
        ValidateBaseUrl(request.BaseUrl, errors);
        if (request.Credential is null
            || request.Credential.Length is < 16 or > 4096)
        {
            errors["/credential"] = ["The Account credential length is invalid."];
        }

        ValidateInteger(request.MaxConcurrency, 1, 10000, "/max_concurrency", errors);
        if (request.Priority.HasValue)
        {
            ValidateInteger(request.Priority.Value, -100000, 100000, "/priority", errors);
        }
        if (request.Weight.HasValue)
        {
            ValidateInteger(request.Weight.Value, 1, 100000, "/weight", errors);
        }
        if (!Enum.IsDefined(request.Provider))
        {
            errors["/provider"] = ["The upstream provider is invalid."];
        }
        return errors;
    }

    private static Dictionary<string, IReadOnlyList<string>> Validate(
        AccountUpdateWireRequest request)
    {
        Dictionary<string, IReadOnlyList<string>> errors = new(StringComparer.Ordinal);
        bool hasMutableField = request.Name.HasValue
            || request.BaseUrl.HasValue
            || request.Credential.HasValue
            || request.Status.HasValue
            || request.MaxConcurrency.HasValue
            || request.Priority.HasValue
            || request.Weight.HasValue;
        if (!hasMutableField)
        {
            errors["/"] = ["At least one mutable Account field is required."];
        }
        if (request.Name.HasValue)
        {
            ValidateName(request.Name.Value, errors);
        }
        if (request.BaseUrl.HasValue)
        {
            ValidateBaseUrl(request.BaseUrl.Value, errors);
        }
        if (request.Credential.HasValue
            && (request.Credential.Value is null
                || request.Credential.Value.Length is < 16 or > 4096))
        {
            errors["/credential"] = ["The Account credential length is invalid."];
        }
        if (request.Status.HasValue
            && request.Status.Value is not ("active" or "disabled"))
        {
            errors["/status"] = ["The Account status is invalid."];
        }
        if (request.MaxConcurrency.HasValue)
        {
            ValidateInteger(
                request.MaxConcurrency.Value,
                1,
                10000,
                "/max_concurrency",
                errors);
        }
        if (request.Priority.HasValue)
        {
            ValidateInteger(request.Priority.Value, -100000, 100000, "/priority", errors);
        }
        if (request.Weight.HasValue)
        {
            ValidateInteger(request.Weight.Value, 1, 100000, "/weight", errors);
        }

        bool reasonRequired = request.Credential.HasValue || request.Status.HasValue;
        if (reasonRequired && !request.Reason.HasValue)
        {
            errors["/reason"] = [
                "Credential and status changes require a non-blank reason.",
            ];
        }
        if (request.Reason.HasValue
            && (string.IsNullOrWhiteSpace(request.Reason.Value)
                || request.Reason.Value.Length > 500
                || request.Reason.Value.Any(static character => character is '\r' or '\n')))
        {
            errors["/reason"] = [
                "The reason must be non-blank and at most 500 characters.",
            ];
        }
        return errors;
    }

    private static void ValidateName(
        string? value,
        Dictionary<string, IReadOnlyList<string>> errors)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 100
            || value.Any(char.IsControl))
        {
            errors["/name"] = ["An Account name of at most 100 characters is required."];
        }
    }

    private static void ValidateBaseUrl(
        string? value,
        Dictionary<string, IReadOnlyList<string>> errors)
    {
        if (value is null)
        {
            errors["/base_url"] = ["The Account Base URL is invalid."];
            return;
        }

        try
        {
            _ = AccountInput.BaseUrl(value);
        }
        catch (ArgumentException)
        {
            errors["/base_url"] = ["The Account Base URL is invalid."];
        }
    }

    private static void ValidateInteger(
        long value,
        long minimum,
        long maximum,
        string pointer,
        Dictionary<string, IReadOnlyList<string>> errors)
    {
        if (value < minimum || value > maximum)
        {
            errors[pointer] = ["The value is outside the supported range."];
        }
    }

    private static AccountActor ToAccountActor(HttpContext context)
    {
        AuthenticatedSupplyActor actor = SupplyHttp.RequireActor(context);
        AccountControlRole role = actor.Role switch
        {
            "admin" => AccountControlRole.Admin,
            "operator" => AccountControlRole.Operator,
            "auditor" => AccountControlRole.Auditor,
            "user" => AccountControlRole.User,
            _ => throw new InvalidOperationException("The Supply role is invalid."),
        };
        return new AccountActor(actor.UserId, role, actor.TokenVersion);
    }

    private static SupplyProvider ToProvider(ContractProvider provider) => provider switch
    {
        ContractProvider.Openai => SupplyProvider.OpenAi,
        ContractProvider.OpenaiCompatible => SupplyProvider.OpenAiCompatible,
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    private static AccountLifecycle ToLifecycle(string status) => status switch
    {
        "active" => AccountLifecycle.Active,
        "disabled" => AccountLifecycle.Disabled,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static PoolAI.Contracts.Generated.Account ToContract(AccountView view) => new()
    {
        Id = view.Id.Value,
        Name = view.Name,
        Platform = "openai",
        Provider = view.Provider switch
        {
            SupplyProvider.OpenAi => ContractProvider.Openai,
            SupplyProvider.OpenAiCompatible => ContractProvider.OpenaiCompatible,
            _ => throw new ArgumentOutOfRangeException(nameof(view)),
        },
        AccountType = "api_key",
        BaseUrl = view.BaseUrl,
        CredentialPrefix = view.CredentialPrefix,
        Status = view.Status switch
        {
            AccountLifecycle.Active => ContractAccountStatus.Active,
            AccountLifecycle.Disabled => ContractAccountStatus.Disabled,
            AccountLifecycle.Retired => ContractAccountStatus.Retired,
            _ => throw new ArgumentOutOfRangeException(nameof(view)),
        },
        Health = new ContractAccountHealth
        {
            Status = view.Health.Status switch
            {
                SupplyAccountHealth.Unknown => ContractAccountHealthStatus.Unknown,
                SupplyAccountHealth.Healthy => ContractAccountHealthStatus.Healthy,
                SupplyAccountHealth.Degraded => ContractAccountHealthStatus.Degraded,
                SupplyAccountHealth.Cooling => ContractAccountHealthStatus.Cooling,
                SupplyAccountHealth.Unhealthy => ContractAccountHealthStatus.Unhealthy,
                _ => throw new ArgumentOutOfRangeException(nameof(view)),
            },
            RetryAt = view.Health.RetryAt,
            LastCheckedAt = view.Health.LastCheckedAt,
        },
        ActiveLeases = view.ActiveLeases,
        MaxConcurrency = view.MaxConcurrency,
        Priority = view.Priority,
        Weight = view.Weight,
        Version = view.Version,
        CreatedAt = view.CreatedAt,
        UpdatedAt = view.UpdatedAt,
    };

    internal sealed class AccountCreateWireRequest
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("provider")]
        public required ContractProvider Provider { get; init; }

        [JsonPropertyName("base_url")]
        public required string BaseUrl { get; init; }

        [JsonPropertyName("credential")]
        public required string Credential { get; init; }

        [JsonPropertyName("max_concurrency")]
        public required long MaxConcurrency { get; init; }

        [JsonPropertyName("priority")]
        public Optional<long> Priority { get; init; }

        [JsonPropertyName("weight")]
        public Optional<long> Weight { get; init; }
    }

    internal sealed class AccountUpdateWireRequest
    {
        [JsonPropertyName("name")]
        public Optional<string> Name { get; init; }

        [JsonPropertyName("base_url")]
        public Optional<string> BaseUrl { get; init; }

        [JsonPropertyName("credential")]
        public Optional<string> Credential { get; init; }

        [JsonPropertyName("status")]
        public Optional<string> Status { get; init; }

        [JsonPropertyName("max_concurrency")]
        public Optional<long> MaxConcurrency { get; init; }

        [JsonPropertyName("priority")]
        public Optional<long> Priority { get; init; }

        [JsonPropertyName("weight")]
        public Optional<long> Weight { get; init; }

        [JsonPropertyName("reason")]
        public Optional<string> Reason { get; init; }
    }
}
#pragma warning restore MA0051
