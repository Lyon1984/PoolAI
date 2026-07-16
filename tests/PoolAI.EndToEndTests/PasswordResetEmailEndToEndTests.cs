using System.Net;
using System.Net.Http.Json;

namespace PoolAI.EndToEndTests;

public sealed class PasswordResetEmailEndToEndTests
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Redis")]
    public async Task PasswordResetEmailIsNonEnumeratingFencedAndSingleUse()
    {
        // Governing contracts: DEC-033 and AC-035. This host-level regression
        // closes the public HTTP and fact-cardinality portion; the separately
        // named Integration test exercises STARTTLS retry/fencing and token use.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PasswordResetHttpEndToEndEnvironment environment =
            await PasswordResetHttpEndToEndEnvironment.CreateAsync(cancellationToken)
                .ConfigureAwait(true);
        PasswordResetHttpEndToEndEnvironment.FactCounts before = await environment
            .ReadFactCountsAsync(cancellationToken).ConfigureAwait(true);

        AcceptedResponse active = await PostForgotAsync(
            environment,
            environment.ActiveEmail,
            cancellationToken).ConfigureAwait(true);
        AcceptedResponse missing = await PostForgotAsync(
            environment,
            environment.MissingEmail,
            cancellationToken).ConfigureAwait(true);
        AcceptedResponse disabled = await PostForgotAsync(
            environment,
            environment.DisabledEmail,
            cancellationToken).ConfigureAwait(true);

        AssertSameAcceptedSemantics(active, missing);
        AssertSameAcceptedSemantics(active, disabled);
        await AssertOnlyActiveUserCreatedFactsAsync(
            environment,
            before,
            cancellationToken).ConfigureAwait(true);
    }

    private static async ValueTask<AcceptedResponse> PostForgotAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        string email,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await environment.Client.PostAsJsonAsync(
            "/api/v1/auth/forgot-password",
            new { email },
            cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        string requestId = Assert.Single(response.Headers.GetValues("X-Request-Id"));
        Assert.True(Guid.TryParse(requestId, out Guid parsedRequestId));
        Assert.NotEqual(Guid.Empty, parsedRequestId);
        return new AcceptedResponse(
            response.StatusCode,
            body,
            response.Content.Headers.ContentType?.ToString(),
            response.Content.Headers.ContentLength,
            response.Headers.RetryAfter?.ToString(),
            response.Headers.Location?.ToString(),
            response.Headers.WwwAuthenticate.Count);
    }

    private static void AssertSameAcceptedSemantics(
        AcceptedResponse expected,
        AcceptedResponse actual)
    {
        Assert.Equal(HttpStatusCode.Accepted, expected.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, actual.StatusCode);
        Assert.Equal(expected.Body, actual.Body);
        Assert.Equal(expected.ContentType, actual.ContentType);
        Assert.Equal(expected.ContentLength, actual.ContentLength);
        Assert.Equal(expected.RetryAfter, actual.RetryAfter);
        Assert.Equal(expected.Location, actual.Location);
        Assert.Equal(expected.AuthenticationChallengeCount, actual.AuthenticationChallengeCount);
        Assert.Empty(actual.Body);
        Assert.Null(actual.RetryAfter);
        Assert.Null(actual.Location);
        Assert.Equal(0, actual.AuthenticationChallengeCount);
    }

    private static async ValueTask AssertOnlyActiveUserCreatedFactsAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        PasswordResetHttpEndToEndEnvironment.FactCounts before,
        CancellationToken cancellationToken)
    {
        PasswordResetHttpEndToEndEnvironment.FactCounts after = await environment
            .ReadFactCountsAsync(cancellationToken).ConfigureAwait(false);
        Assert.Equal(before.Tokens + 1, after.Tokens);
        Assert.Equal(before.Emails + 1, after.Emails);
        Assert.Equal(before.Audits + 1, after.Audits);
        Assert.Equal(before.Events + 1, after.Events);
        Assert.Equal(before.IdempotencyRecords, after.IdempotencyRecords);
        Assert.Equal(0, await environment.CountUsersByNormalizedEmailAsync(
            environment.MissingNormalizedEmail,
            cancellationToken).ConfigureAwait(false));
        Assert.Equal(2, await environment.CountPasswordResetFactsForUserAsync(
            environment.ActiveUserId,
            cancellationToken).ConfigureAwait(false));
        Assert.Equal(0, await environment.CountPasswordResetFactsForUserAsync(
            environment.DisabledUserId,
            cancellationToken).ConfigureAwait(false));
    }

    private sealed record AcceptedResponse(
        HttpStatusCode StatusCode,
        string Body,
        string? ContentType,
        long? ContentLength,
        string? RetryAfter,
        string? Location,
        int AuthenticationChallengeCount);
}
