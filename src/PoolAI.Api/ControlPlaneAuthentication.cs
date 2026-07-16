using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

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
        services.AddAuthorization();
        return services;
    }

    private static void ConfigureJwtBearer(
        JwtBearerOptions options,
        IConfiguration configuration,
        byte[] signingKey)
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = configuration["Auth:Jwt:Issuer"] ?? "PoolAI",
            ValidateAudience = true,
            ValidAudience = configuration["Auth:Jwt:Audience"] ?? "PoolAI.Web",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(signingKey),
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(configuration.GetValue(
                "Auth:Jwt:ClockSkewSeconds",
                30)),
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = "role",
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = ValidateRequiredClaimsAsync,
            OnChallenge = WriteChallengeAsync,
            OnForbidden = WriteForbiddenAsync,
        };
    }

    private static Task ValidateRequiredClaimsAsync(TokenValidatedContext context)
    {
        ClaimsPrincipal principal = context.Principal!;
        string? subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        string? role = principal.FindFirstValue("role");
        string? tokenVersion = principal.FindFirstValue("token_version");
        if (!Guid.TryParse(subject, out Guid subjectId)
            || subjectId == Guid.Empty
            || role is not ("admin" or "operator" or "auditor" or "user")
            || !long.TryParse(
                tokenVersion,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long parsedTokenVersion)
            || parsedTokenVersion <= 0)
        {
            context.Fail("The access token is missing required identity claims.");
        }

        return Task.CompletedTask;
    }

    private static Task WriteChallengeAsync(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
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

    private static Task WriteForbiddenAsync(ForbiddenContext context) =>
        ControlPlaneProblemWriter.WriteAsync(
            context.HttpContext,
            StatusCodes.Status403Forbidden,
            "role_required",
            "Required role missing",
            "The authenticated user does not have the required role.",
            retryable: false);
}
