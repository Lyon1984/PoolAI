#pragma warning disable MA0051 // Route adapters keep the frozen request mapping visible.
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PoolAI.BuildingBlocks;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.Supply.Application;
using PoolAI.Modules.Supply.Domain;
using ContractConfiguration = PoolAI.Contracts.Generated.GroupSupplyConfiguration;

namespace PoolAI.Modules.Supply.Endpoints;

internal static class GroupSupplyConfigurationEndpointMappings
{
    internal static IEndpointRouteBuilder MapGroupSupplyConfigurationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder configurations = endpoints.MapGroup(
            "/api/v1/admin/groups/{groupId:guid}/supply-configuration");
        configurations.AddEndpointFilter(static async (invocation, next) =>
        {
            HttpContext context = invocation.HttpContext;
            return SupplyHttp.TryGetActor(context, out _)
                ? await next(invocation).ConfigureAwait(false)
                : SupplyHttp.InvalidUserToken(context);
        });
        configurations.MapGet("/", GetAsync)
            .RequireAuthorization(RequireReadRole)
            .WithName("adminGetGroupSupplyConfiguration");
        configurations.MapPost("/", CreateAsync)
            .RequireAuthorization(RequireAdmin)
            .WithName("adminCreateGroupSupplyConfiguration");
        configurations.MapMethods("/", [HttpMethods.Patch], PatchAsync)
            .RequireAuthorization(RequireAdmin)
            .WithName("adminUpdateGroupSupplyConfiguration");
        return endpoints;
    }

    private static void RequireReadRole(AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser().RequireRole("admin", "operator", "auditor");

    private static void RequireAdmin(AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser().RequireRole("admin");

    private static async Task<IResult> GetAsync(
        HttpContext context,
        IGetGroupSupplyConfigurationUseCase useCase,
        Guid groupId)
    {
        if (!TryGetGroupId(context, groupId, out EntityId id, out IResult? failure))
        {
            return failure!;
        }

        Result<GroupSupplyConfigurationView> result = await useCase.ExecuteAsync(
            new GetGroupSupplyConfigurationQuery(ToActor(context), id),
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
        ICreateGroupSupplyConfigurationUseCase useCase,
        Guid groupId,
        GroupSupplyConfigurationCreateRequest request)
    {
        IResult? failure = SupplyHttp.RequireContentType(context, "application/json");
        if (failure is not null)
        {
            return failure;
        }

        if (!TryGetGroupId(context, groupId, out EntityId id, out failure))
        {
            return failure!;
        }

        Dictionary<string, IReadOnlyList<string>> errors =
            ValidateBindings(request.AccountBindings);
        ValidateOptionalId(request.ChannelId, "/channel_id", "Channel", errors);
        if (errors.Count != 0)
        {
            return SupplyHttp.ValidationProblem(context, errors);
        }

        if (!SupplyHttp.TryGetIdempotencyKey(context, out string? key, out failure))
        {
            return failure!;
        }

        Result<SupplyCommandOutcome<GroupSupplyConfigurationView>> result =
            await useCase.ExecuteAsync(
                new CreateGroupSupplyConfigurationCommand(
                    SupplyHttp.RequestId(context),
                    ToActor(context),
                    key!,
                    id,
                    ToOptionalEntityId(request.ChannelId),
                    ToBindings(request.AccountBindings),
                    SupplyHttp.RemoteIp(context),
                    SupplyHttp.UserAgent(context)),
                context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return SupplyHttp.FromError(context, result.Error);
        }

        SupplyCommandOutcome<GroupSupplyConfigurationView> outcome = result.Value;
        context.Response.Headers.ETag = outcome.ETag;
        context.Response.Headers.Location = outcome.Location
            ?? $"/api/v1/admin/groups/{id.Value:D}/supply-configuration";
        return Results.Json(ToContract(outcome.Value), statusCode: outcome.StatusCode);
    }

    private static async Task<IResult> PatchAsync(
        HttpContext context,
        IPatchGroupSupplyConfigurationUseCase useCase,
        Guid groupId,
        GroupSupplyConfigurationUpdateRequest request)
    {
        IResult? failure = SupplyHttp.RequireContentType(
            context,
            "application/merge-patch+json");
        if (failure is not null)
        {
            return failure;
        }

        if (!TryGetGroupId(context, groupId, out EntityId id, out failure))
        {
            return failure!;
        }

        Dictionary<string, IReadOnlyList<string>> errors =
            new(StringComparer.Ordinal);
        if (!request.ChannelId.HasValue && !request.AccountBindings.HasValue)
        {
            errors["/"] = [
                "Either channel_id or account_bindings must be supplied.",
            ];
        }

        if (request.ChannelId.HasValue)
        {
            ValidateOptionalId(
                request.ChannelId.Value,
                "/channel_id",
                "Channel",
                errors);
        }

        if (request.AccountBindings.HasValue)
        {
            AddErrors(
                errors,
                ValidateBindings(request.AccountBindings.Value));
        }

        ValidateReason(request.Reason, errors);
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

        Result<SupplyCommandOutcome<GroupSupplyConfigurationView>> result =
            await useCase.ExecuteAsync(
                new PatchGroupSupplyConfigurationCommand(
                    SupplyHttp.RequestId(context),
                    ToActor(context),
                    key!,
                    id,
                    expectedVersion,
                    request.ChannelId.HasValue,
                    request.ChannelId.HasValue
                        ? ToOptionalEntityId(request.ChannelId.Value)
                        : null,
                    request.AccountBindings.HasValue,
                    request.AccountBindings.HasValue
                        ? ToBindings(request.AccountBindings.Value!)
                        : null,
                    request.Reason,
                    SupplyHttp.RemoteIp(context),
                    SupplyHttp.UserAgent(context)),
                context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return SupplyHttp.FromError(context, result.Error);
        }

        SupplyCommandOutcome<GroupSupplyConfigurationView> outcome = result.Value;
        context.Response.Headers.ETag = outcome.ETag;
        return Results.Json(ToContract(outcome.Value), statusCode: outcome.StatusCode);
    }

    private static bool TryGetGroupId(
        HttpContext context,
        Guid groupId,
        out EntityId id,
        out IResult? failure) => SupplyHttp.TryGetEntityId(
        context,
        groupId,
        "/groupId",
        "Group",
        out id,
        out failure);

    private static Dictionary<string, IReadOnlyList<string>> ValidateBindings(
        IReadOnlyList<GroupAccountBinding>? bindings)
    {
        Dictionary<string, IReadOnlyList<string>> errors =
            new(StringComparer.Ordinal);
        if (bindings is null)
        {
            errors["/account_bindings"] = ["Account bindings are required."];
            return errors;
        }

        if (bindings.Any(static binding => binding is null))
        {
            errors["/account_bindings"] = ["Account bindings cannot contain null."];
            return errors;
        }

        for (int index = 0; index < bindings.Count; index++)
        {
            GroupAccountBinding binding = bindings[index];
            if (binding.AccountId == Guid.Empty)
            {
                errors[$"/account_bindings/{index}/account_id"] = [
                    "The Account identifier must be a non-empty UUID.",
                ];
            }

            if (binding.PriorityOverride is < -100000 or > 100000)
            {
                errors[$"/account_bindings/{index}/priority_override"] = [
                    "The priority override must be between -100000 and 100000.",
                ];
            }

            if (binding.WeightOverride is < 1 or > 100000)
            {
                errors[$"/account_bindings/{index}/weight_override"] = [
                    "The weight override must be between 1 and 100000.",
                ];
            }
        }

        if (bindings
            .GroupBy(static binding => binding.AccountId)
            .Any(static group => group.Count() > 1))
        {
            errors["/account_bindings"] = [
                "Account identifiers must be unique within a Supply configuration.",
            ];
        }

        return errors;
    }

    private static void ValidateOptionalId(
        Guid? value,
        string pointer,
        string resourceName,
        Dictionary<string, IReadOnlyList<string>> errors)
    {
        if (value == Guid.Empty)
        {
            errors[pointer] = [
                $"The {resourceName} identifier must be null or a non-empty UUID.",
            ];
        }
    }

    private static void ValidateReason(
        string? value,
        Dictionary<string, IReadOnlyList<string>> errors)
    {
        try
        {
            _ = GroupSupplyInput.Reason(value!);
        }
        catch (ArgumentException)
        {
            errors["/reason"] = ["The change reason is invalid."];
        }
    }

    private static void AddErrors(
        Dictionary<string, IReadOnlyList<string>> target,
        IReadOnlyDictionary<string, IReadOnlyList<string>> source)
    {
        foreach ((string pointer, IReadOnlyList<string> messages) in source)
        {
            target[pointer] = messages;
        }
    }

    private static EntityId? ToOptionalEntityId(Guid? value) =>
        value is Guid id ? new EntityId(id) : null;

    private static GroupSupplyBindingView[] ToBindings(
        IReadOnlyList<GroupAccountBinding> bindings) => bindings
        .Select(static binding => new GroupSupplyBindingView(
            new EntityId(binding.AccountId),
            binding.Enabled,
            binding.PriorityOverride is long priority
                ? checked((int)priority)
                : null,
            binding.WeightOverride is long weight
                ? checked((int)weight)
                : null))
        .ToArray();

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

    private static ContractConfiguration ToContract(
        GroupSupplyConfigurationView view) => new()
        {
            GroupId = view.GroupId.Value,
            ChannelId = view.ChannelId?.Value,
            AccountBindings = view.AccountBindings
                .Select(static binding => new GroupAccountBinding
                {
                    AccountId = binding.AccountId.Value,
                    Enabled = binding.Enabled,
                    PriorityOverride = binding.PriorityOverride,
                    WeightOverride = binding.WeightOverride,
                })
                .ToArray(),
            Version = view.Version,
            CreatedAt = view.CreatedAt,
            UpdatedAt = view.UpdatedAt,
        };
}
#pragma warning restore MA0051
