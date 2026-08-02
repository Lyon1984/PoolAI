using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PoolAI.Modules.Operations.Application;
using ContractOutboxReplayReceipt = PoolAI.Contracts.Generated.OutboxReplayReceipt;

namespace PoolAI.Modules.Operations.Endpoints;

public static class OutboxReplayEndpointMappings
{
    public static IEndpointRouteBuilder MapOutboxReplayEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        RouteGroupBuilder outbox = endpoints.MapGroup("/api/v1/admin/outbox-messages");
        outbox.AddEndpointFilter(static async (invocation, next) =>
        {
            HttpContext context = invocation.HttpContext;
            return OutboxReplayHttp.TryGetActor(context, out _)
                ? await next(invocation).ConfigureAwait(false)
                : OutboxReplayHttp.InvalidUserToken(context);
        });
        outbox.MapPost("/{messageId:guid}/replay", ReplayDeadAsync)
            .RequireAuthorization(RequireAdmin)
            .WithName("adminReplayDeadOutboxMessage");
        return endpoints;
    }

    private static void RequireAdmin(AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser().RequireRole("admin");

    private static async Task<IResult> ReplayDeadAsync(
        HttpContext context,
        IReplayDeadOutboxUseCase useCase,
        Guid messageId)
    {
        if (!OutboxReplayHttp.TryGetEntityId(
                context,
                messageId,
                out EntityId sourceMessageId,
                out IResult? failure))
        {
            return failure!;
        }

        OutboxReplayHttp.ReplayRequestReadResult request =
            await OutboxReplayHttp.ReadReplayRequestAsync(context).ConfigureAwait(false);
        if (request.Failure is not null)
        {
            return request.Failure;
        }

        if (!OutboxReplayHttp.TryGetIdempotencyKey(
                context,
                out string? idempotencyKey,
                out failure))
        {
            return failure!;
        }

        Result<OutboxReplayOutcome> result = await useCase.ExecuteAsync(
            new ReplayDeadOutboxCommand(
                OutboxReplayHttp.RequestId(context),
                OutboxReplayHttp.RequireActor(context),
                idempotencyKey!,
                sourceMessageId,
                request.Reason!,
                OutboxReplayHttp.RemoteIp(context),
                OutboxReplayHttp.UserAgent(context)),
            context.RequestAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return OutboxReplayHttp.FromError(context, result.Error);
        }

        OutboxReplayOutcome outcome = result.Value;
        return Results.Json(
            new ContractOutboxReplayReceipt
            {
                MessageId = outcome.MessageId.Value,
                EventSequence = outcome.EventSequence.ToString(CultureInfo.InvariantCulture),
                ReplayOf = outcome.ReplayOf.Value,
                Status = "pending",
            },
            statusCode: StatusCodes.Status202Accepted);
    }
}
