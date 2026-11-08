using Microsoft.IdentityModel.Tokens;

namespace Ruoyu.Study.Common.Authentication;

public static class IdentityTokenValidationParametersFactory
{
    public static TokenValidationParameters Create(
        IdentityAuthenticationOptions options,
        IEnumerable<SecurityKey>? signingKeys = null,
        string? nameClaimType = null,
        string? roleClaimType = null,
        string? audienceOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var validIssuers = options.GetValidIssuers();
        var parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            ValidateIssuer = true,
            ValidIssuers = validIssuers,
            // Discovery supplies signing keys, but must not expand the explicit
            // issuer trust snapshot (including the configured migration window).
            IssuerValidator = (issuer, _, _) =>
                validIssuers.Contains(issuer, StringComparer.Ordinal)
                    ? issuer
                    : throw new SecurityTokenInvalidIssuerException(
                        "Token issuer is not trusted by IdentityService:Issuer or AdditionalValidIssuers."),
            ValidateAudience = true,
            // A non-null override replaces the configured audience with an
            // exact-match requirement; an empty override combined with
            // ValidateAudience=true rejects every token (fail closed).
            ValidAudience = audienceOverride is null
                ? options.Audience
                : audienceOverride.Trim(),
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(options.ClockSkewSeconds)
        };

        if (!string.IsNullOrWhiteSpace(nameClaimType))
        {
            parameters.NameClaimType = nameClaimType;
        }

        if (!string.IsNullOrWhiteSpace(roleClaimType))
        {
            parameters.RoleClaimType = roleClaimType;
        }

        return parameters;
    }
}
