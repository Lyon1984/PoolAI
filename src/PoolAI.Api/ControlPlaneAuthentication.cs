using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using PoolAI.Modules.Identity.Abstractions;
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
internal sealed partial class ControlPlaneJwtEvents : JwtBearerEvents
{
    private const string DependencyUnavailableItem = "poolai.auth.session-dependency-unavailable";
    private readonly IAccessSessionValidator _sessionValidator;
    private readonly ILogger<ControlPlaneJwtEvents> _logger;

    public ControlPlaneJwtEvents(
        IAccessSessionValidator sessionValidator,
        ILogger<ControlPlaneJwtEvents> logger)
    {
        _sessionValidator = sessionValidator
            ?? throw new ArgumentNullException(nameof(sessionValidator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async Task TokenValidated(TokenValidatedContext context)
    {
        ClaimsPrincipal principal = context.Principal!;
        string? subject = SingleClaimValue(principal, JwtRegisteredClaimNames.Sub);
        string? role = SingleClaimValue(principal, "role");
        string? tokenVersion = SingleClaimValue(principal, "token_version");
        string? sessionFamily = SingleClaimValue(principal, "sid");
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
            UserStatusSnapshot? authorization = await _sessionValidator
                .ReadCanonicalAuthorizationAsync(
                    subjectId,
                    familyId,
                    parsedTokenVersion,
                    context.HttpContext.RequestAborted)
                .ConfigureAwait(false);
            if (authorization is null
                || authorization.UserId.Value != subjectId
                || authorization.Lifecycle != UserLifecycle.Active
                || authorization.TokenVersion != parsedTokenVersion
                || authorization.Version <= 0)
            {
                context.Fail("The access-token session family is no longer active.");
                return;
            }

            ReplaceCanonicalClaims(
                principal,
                authorization.Role,
                authorization.TokenVersion);
        }
        catch (OperationCanceledException)
            when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogCanonicalAuthorizationFailure(
                _logger,
                exception.GetType().FullName ?? exception.GetType().Name);
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

    private static string? SingleClaimValue(ClaimsPrincipal principal, string claimType)
    {
        Claim[] claims = principal.FindAll(claimType).Take(2).ToArray();
        return claims.Length == 1 ? claims[0].Value : null;
    }

    private static void ReplaceCanonicalClaims(
        ClaimsPrincipal principal,
        SystemRole role,
        long tokenVersion)
    {
        ClaimsIdentity identity = principal.Identity as ClaimsIdentity
            ?? throw new InvalidOperationException(
                "The validated access token has no mutable claims identity.");
        foreach (ClaimsIdentity candidate in principal.Identities)
        {
            foreach (Claim claim in candidate.Claims
                         .Where(static claim => claim.Type is "role" or "token_version"
                             || string.Equals(
                                 claim.Type,
                                 ClaimTypes.Role,
                                 StringComparison.Ordinal))
                         .ToArray())
            {
                candidate.RemoveClaim(claim);
            }
        }

        identity.AddClaim(new Claim("role", RoleCode(role)));
        identity.AddClaim(new Claim(
            "token_version",
            tokenVersion.ToString(CultureInfo.InvariantCulture)));
    }

    private static string RoleCode(SystemRole role) => role switch
    {
        SystemRole.Admin => "admin",
        SystemRole.Operator => "operator",
        SystemRole.Auditor => "auditor",
        SystemRole.User => "user",
        _ => throw new InvalidOperationException(
            "The canonical access authorization has an unknown role."),
    };

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Error,
        Message = "Canonical control-plane authorization read failed with {ExceptionType}.")]
    private static partial void LogCanonicalAuthorizationFailure(
        ILogger logger,
        string exceptionType);
}
#pragma warning restore MA0048
