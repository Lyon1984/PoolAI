using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using PoolAI.Modules.Identity.Application;

namespace PoolAI.Api;

internal static class ControlPlaneAuthentication
{
    public static IServiceCollection AddControlPlaneAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        byte[] signingKey = Convert.FromBase64String(configuration["Auth:Jwt:SigningKey"]!);
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options => ConfigureJwtBearer(
                options,
                configuration,
                signingKey));
        services.AddSingleton<ControlPlaneJwtEvents>();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<TimeProvider>(static (options, timeProvider) =>
                options.TokenValidationParameters.LifetimeValidator =
                    (notBefore, expires, _, parameters) => IsLifetimeValid(
                        notBefore,
                        expires,
                        parameters.ClockSkew,
                        timeProvider));
        services.AddAuthorization();
        return services;
    }

    private static void ConfigureJwtBearer(
        JwtBearerOptions options,
        IConfiguration configuration,
        byte[] signingKey)
    {
        options.MapInboundClaims = false;
        options.EventsType = typeof(ControlPlaneJwtEvents);
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = configuration["Auth:Jwt:Issuer"] ?? "PoolAI",
            ValidateAudience = true,
            ValidAudience = configuration["Auth:Jwt:Audience"] ?? "PoolAI.Web",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(signingKey),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(configuration.GetValue(
                "Auth:Jwt:ClockSkewSeconds",
                30)),
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = "role",
        };
    }

    private static bool IsLifetimeValid(
        DateTime? notBefore,
        DateTime? expires,
        TimeSpan clockSkew,
        TimeProvider timeProvider)
    {
        if (expires is null || notBefore > expires)
        {
            return false;
        }

        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        return (notBefore is null || notBefore.Value <= now + clockSkew)
            && expires.Value >= now - clockSkew;
    }
}

#pragma warning disable MA0048 // Authentication configuration and its scheme events form one cohesive surface.
internal sealed class ControlPlaneJwtEvents : JwtBearerEvents
{
    private const string DependencyUnavailableItem = "poolai.auth.session-dependency-unavailable";
    private readonly IAccessSessionValidator _sessionValidator;

    public ControlPlaneJwtEvents(IAccessSessionValidator sessionValidator)
    {
        _sessionValidator = sessionValidator
            ?? throw new ArgumentNullException(nameof(sessionValidator));
    }

    public override async Task TokenValidated(TokenValidatedContext context)
    {
        ClaimsPrincipal principal = context.Principal!;
        string? subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        string? role = principal.FindFirstValue("role");
        string? tokenVersion = principal.FindFirstValue("token_version");
        string? sessionFamily = principal.FindFirstValue("sid");
        if (!Guid.TryParse(subject, out Guid subjectId)
            || subjectId == Guid.Empty
            || !Guid.TryParse(sessionFamily, out Guid familyId)
            || familyId == Guid.Empty
            || role is not ("admin" or "operator" or "auditor" or "user")
            || !long.TryParse(
                tokenVersion,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long parsedTokenVersion)
            || parsedTokenVersion <= 0)
        {
            context.Fail("The access token is missing required identity claims.");
            return;
        }

        try
        {
            if (!await _sessionValidator.IsActiveAsync(
                    subjectId,
                    familyId,
                    parsedTokenVersion,
                    context.HttpContext.RequestAborted)
                .ConfigureAwait(false))
            {
                context.Fail("The access-token session family is no longer active.");
            }
        }
        catch (OperationCanceledException)
            when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            context.HttpContext.Items[DependencyUnavailableItem] = true;
            context.Fail("The access-token session authority is unavailable.");
        }
    }

    public override Task Challenge(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
        if (context.HttpContext.Items.ContainsKey(DependencyUnavailableItem))
        {
            return ControlPlaneProblemWriter.WriteAsync(
                context.HttpContext,
                StatusCodes.Status503ServiceUnavailable,
                "dependency_unavailable",
                "Authentication dependency unavailable",
                "The session authority is temporarily unavailable.",
                retryable: true,
                retryAfterSeconds: 1);
        }

        context.Response.Headers.WWWAuthenticate = "Bearer";
        string code = context.AuthenticateFailure is null
            ? "authentication_required"
            : "invalid_user_token";
        string detail = context.AuthenticateFailure is null
            ? "Authentication credentials are required."
            : "The user access token is invalid or expired.";
        return ControlPlaneProblemWriter.WriteAsync(
            context.HttpContext,
            StatusCodes.Status401Unauthorized,
            code,
            "Authentication required",
            detail,
            retryable: false);
    }

    public override Task Forbidden(ForbiddenContext context) =>
        ControlPlaneProblemWriter.WriteAsync(
            context.HttpContext,
            StatusCodes.Status403Forbidden,
            "role_required",
            "Required role missing",
            "The authenticated user does not have the required role.",
            retryable: false);
}
#pragma warning restore MA0048
