#pragma warning disable MA0051 // Route adapters keep the frozen request mapping visible.
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PoolAI.BuildingBlocks;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.Supply.Application;
using PoolAI.Modules.Supply.Domain;
using ContractChannel = PoolAI.Contracts.Generated.Channel;
using ContractChannelPage = PoolAI.Contracts.Generated.ChannelPage;
using ContractProvider = PoolAI.Contracts.Generated.UpstreamProvider;
using SupplyProvider = PoolAI.Modules.Supply.Abstractions.UpstreamProvider;

namespace PoolAI.Modules.Supply.Endpoints;

internal static class SupplyChannelEndpointMappings
{
    internal static IEndpointRouteBuilder MapSupplyChannelEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder channels = endpoints.MapGroup("/api/v1/admin/channels");
        channels.AddEndpointFilter(static async (invocation, next) =>
        {
            HttpContext context = invocation.HttpContext;
            return SupplyHttp.TryGetActor(context, out _)
                ? await next(invocation).ConfigureAwait(false)
                : SupplyHttp.InvalidUserToken(context);
        });
        channels.MapGet("/", ListAsync)
            .RequireAuthorization(RequireReadRole)
            .WithName("adminListChannels");
        channels.MapPost("/", CreateAsync)
            .RequireAuthorization(RequireWriteRole)
            .WithName("adminCreateChannel");
        channels.MapGet("/{channelId:guid}", GetAsync)
            .RequireAuthorization(RequireReadRole)
            .WithName("adminGetChannel");
        channels.MapMethods("/{channelId:guid}", [HttpMethods.Patch], UpdateAsync)
            .RequireAuthorization(RequireWriteRole)
            .WithName("adminUpdateChannel");
        channels.MapDelete("/{channelId:guid}", RetireAsync)
            .RequireAuthorization(RequireWriteRole)
            .WithName("adminRetireChannel");
        return endpoints;
    }

    private static void RequireReadRole(AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser().RequireRole("admin", "operator", "auditor");

    private static void RequireWriteRole(AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser().RequireRole("admin", "operator");

    private static async Task<IResult> ListAsync(
        HttpContext context,
        IListChannelsUseCase useCase,
        string? cursor,
        string? limit)
    {
        if (!SupplyHttp.TryGetPagination(
                context,
                limit,
                out int parsedLimit,
                out IResult? failure))
        {
            return failure!;
        }

        Result<PoolAI.Modules.Supply.Application.ChannelPage> result =
            await useCase.ExecuteAsync(
                new ListChannelsQuery(ToActor(context), cursor, parsedLimit),
                context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return SupplyHttp.FromError(context, result.Error);
        }

        PoolAI.Modules.Supply.Application.ChannelPage page = result.Value;
        return Results.Ok(new ContractChannelPage
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
        IGetChannelUseCase useCase,
        Guid channelId)
    {
        if (!TryGetChannelId(context, channelId, out EntityId id, out IResult? failure))
        {
            return failure!;
        }

        Result<ChannelView> result = await useCase.ExecuteAsync(
            new GetChannelQuery(ToActor(context), id),
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
        ICreateChannelUseCase useCase,
        ChannelCreateRequest request)
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

        Result<SupplyCommandOutcome<ChannelView>> result = await useCase.ExecuteAsync(
            new CreateChannelCommand(
                SupplyHttp.RequestId(context),
                ToActor(context),
                key!,
                request.Name,
                ToProvider(request.Provider),
                ToCapabilities(request.Capabilities),
                ToMappings(request.ModelMappings),
                SupplyHttp.RemoteIp(context),
                SupplyHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return SupplyHttp.FromError(context, result.Error);
        }

        SupplyCommandOutcome<ChannelView> outcome = result.Value;
        context.Response.Headers.ETag = outcome.ETag;
        context.Response.Headers.Location = outcome.Location
            ?? $"/api/v1/admin/channels/{outcome.Value.Id.Value:D}";
        return Results.Json(ToContract(outcome.Value), statusCode: outcome.StatusCode);
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext context,
        IUpdateChannelUseCase useCase,
        Guid channelId,
        ChannelUpdateRequest request)
    {
        IResult? failure = SupplyHttp.RequireContentType(
            context,
            "application/merge-patch+json");
        if (failure is not null)
        {
            return failure;
        }

        if (!TryGetChannelId(context, channelId, out EntityId id, out failure))
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

        Result<SupplyCommandOutcome<ChannelView>> result = await useCase.ExecuteAsync(
            new UpdateChannelCommand(
                SupplyHttp.RequestId(context),
                ToActor(context),
                key!,
                id,
                expectedVersion,
                request.Name.HasValue,
                request.Name.HasValue ? request.Name.Value : null,
                request.Status.HasValue,
                request.Status.HasValue ? ToLifecycle(request.Status.Value!) : null,
                request.Capabilities.HasValue,
                request.Capabilities.HasValue
                    ? ToCapabilities(request.Capabilities.Value!)
                    : null,
                request.ModelMappings.HasValue,
                request.ModelMappings.HasValue
                    ? ToMappings(request.ModelMappings.Value!)
                    : null,
                request.Reason.HasValue ? request.Reason.Value : null,
                SupplyHttp.RemoteIp(context),
                SupplyHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return SupplyHttp.FromError(context, result.Error);
        }

        SupplyCommandOutcome<ChannelView> outcome = result.Value;
        context.Response.Headers.ETag = outcome.ETag;
        return Results.Json(ToContract(outcome.Value), statusCode: outcome.StatusCode);
    }

    private static async Task<IResult> RetireAsync(
        HttpContext context,
        IRetireChannelUseCase useCase,
        Guid channelId)
    {
        if (!TryGetChannelId(
                context,
                channelId,
                out EntityId id,
                out IResult? failure)
            || !SupplyHttp.TryGetIdempotencyKey(context, out string? key, out failure)
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

        Result<SupplyCommandOutcome> result = await useCase.ExecuteAsync(
            new RetireChannelCommand(
                SupplyHttp.RequestId(context),
                ToActor(context),
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

    private static bool TryGetChannelId(
        HttpContext context,
        Guid channelId,
        out EntityId id,
        out IResult? failure) => SupplyHttp.TryGetEntityId(
        context,
        channelId,
        "/channelId",
        "Channel",
        out id,
        out failure);

    private static Dictionary<string, IReadOnlyList<string>> Validate(
        ChannelCreateRequest request)
    {
        Dictionary<string, IReadOnlyList<string>> errors =
            new(StringComparer.Ordinal);
        ValidateName(request.Name, errors);
        ValidateCapabilities(request.Capabilities, errors);
        ValidateMappings(request.ModelMappings, errors);
        return errors;
    }

    private static Dictionary<string, IReadOnlyList<string>> Validate(
        ChannelUpdateRequest request)
    {
        Dictionary<string, IReadOnlyList<string>> errors =
            new(StringComparer.Ordinal);
        if (!request.Name.HasValue
            && !request.Status.HasValue
            && !request.Capabilities.HasValue
            && !request.ModelMappings.HasValue)
        {
            errors["/"] = ["At least one Channel field must be supplied."];
        }

        if (request.Name.HasValue)
        {
            ValidateName(request.Name.Value, errors);
        }

        if (request.Status.HasValue)
        {
            string? status = request.Status.Value;
            if (status is not ("active" or "disabled"))
            {
                errors["/status"] = ["The Channel status must be active or disabled."];
            }

            ValidateReason(
                request.Reason.HasValue ? request.Reason.Value : null,
                errors);
        }

        if (request.Capabilities.HasValue)
        {
            ValidateCapabilities(request.Capabilities.Value, errors);
        }

        if (request.ModelMappings.HasValue)
        {
            ValidateMappings(request.ModelMappings.Value, errors);
        }

        return errors;
    }

    private static void ValidateName(
        string? value,
        Dictionary<string, IReadOnlyList<string>> errors)
    {
        try
        {
            _ = ChannelInput.Name(value!);
        }
        catch (ArgumentException)
        {
            errors["/name"] = ["The Channel name is invalid."];
        }
    }

    private static void ValidateCapabilities(
        ChannelCapabilities? value,
        Dictionary<string, IReadOnlyList<string>> errors)
    {
        if (value is null)
        {
            errors["/capabilities"] = ["Channel capabilities are required."];
        }
    }

    private static void ValidateMappings(
        IReadOnlyList<ModelMapping>? values,
        Dictionary<string, IReadOnlyList<string>> errors)
    {
        if (values is null)
        {
            errors["/model_mappings"] = ["Channel model mappings are required."];
            return;
        }

        if (values.Any(static value => value is null))
        {
            errors["/model_mappings"] = ["Channel model mappings cannot contain null."];
            return;
        }

        try
        {
            _ = ChannelInput.ModelMappings(ToDomainMappings(values));
        }
        catch (ArgumentException)
        {
            errors["/model_mappings"] = [
                "Channel model mappings must be non-empty, valid, and unique.",
            ];
        }
    }

    private static void ValidateReason(
        string? value,
        Dictionary<string, IReadOnlyList<string>> errors)
    {
        try
        {
            _ = ChannelInput.Reason(value!);
        }
        catch (ArgumentException)
        {
            errors["/reason"] = ["A non-blank reason is required for status changes."];
        }
    }

    private static AccountActor ToActor(HttpContext context)
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

    private static ChannelLifecycle ToLifecycle(string status) => status switch
    {
        "active" => ChannelLifecycle.Active,
        "disabled" => ChannelLifecycle.Disabled,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static PoolAI.Modules.Supply.Abstractions.ChannelCapabilitiesSnapshot
        ToCapabilities(ChannelCapabilities value) => new(
            value.Responses,
            value.ChatCompletions,
            value.FunctionTools,
            value.Streaming);

    private static ChannelModelMappingView[] ToMappings(
        IReadOnlyList<ModelMapping> values) => values
        .Select(static value => new ChannelModelMappingView(
            value.ClientModel,
            value.UpstreamModel))
        .ToArray();

    private static ChannelModelMappingValue[] ToDomainMappings(
        IReadOnlyList<ModelMapping> values) => values
        .Select(static value => new ChannelModelMappingValue(
            value.ClientModel,
            value.UpstreamModel))
        .ToArray();

    private static ContractChannel ToContract(ChannelView view) => new()
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
        Status = view.Status switch
        {
            ChannelLifecycle.Active => "active",
            ChannelLifecycle.Disabled => "disabled",
            ChannelLifecycle.Retired => "retired",
            _ => throw new ArgumentOutOfRangeException(nameof(view)),
        },
        Capabilities = new ChannelCapabilities
        {
            Responses = view.Capabilities.Responses,
            ChatCompletions = view.Capabilities.ChatCompletions,
            FunctionTools = view.Capabilities.FunctionTools,
            Streaming = view.Capabilities.Streaming,
        },
        ModelMappings = view.ModelMappings
            .Select(static mapping => new ModelMapping
            {
                ClientModel = mapping.ClientModel,
                UpstreamModel = mapping.UpstreamModel,
            })
            .ToArray(),
        Version = view.Version,
        CreatedAt = view.CreatedAt,
        UpdatedAt = view.UpdatedAt,
    };
}
#pragma warning restore MA0051
