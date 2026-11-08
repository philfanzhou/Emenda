using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ruoyu.Study.Common.Authentication;

/// <summary>
/// Well-known name of the administrator-audience JWT Bearer scheme. Portal administration
/// consoles forward the administrator's own SignaCore access token, whose audience is the
/// console's application (ADR-0030); a service that accepts those calls (Mistake) lists the
/// console applications in <c>IdentityService:AdminAudience</c>.
/// </summary>
public static class RuoyuAdminApiAuthorization
{
    /// <summary>JWT Bearer scheme that validates only the configured administrator audiences.</summary>
    public const string SchemeName = "RuoyuAdminAudience";
}

public static class RuoyuAdminAudienceJwtBearerExtensions
{
    /// <summary>
    /// Registers a second JWT Bearer scheme that accepts only the application ids listed in
    /// <c>IdentityService:AdminAudience</c> (each matched exactly), with the same
    /// issuer/signing-key/lifetime validation as the shared-audience scheme. The scheme
    /// authenticates only; the caller's own policy decides what it admits and which roles
    /// it requires.
    ///
    /// When no administrator audience is configured the scheme still exists but accepts no
    /// audience at all (fail closed); the shared audience path is unaffected.
    /// </summary>
    public static IServiceCollection AddRuoyuAdminAudienceJwtBearer(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<RuoyuJwtBearerConsumerOptions>? configureConsumer = null)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));

        var identityOptions = configuration
            .GetSection(IdentityAuthenticationOptions.SectionName)
            .Get<IdentityAuthenticationOptions>() ?? new IdentityAuthenticationOptions();
        var validation = new IdentityAuthenticationOptionsValidator()
            .Validate(Options.DefaultName, identityOptions);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                IdentityAuthenticationOptions.SectionName,
                typeof(IdentityAuthenticationOptions),
                validation.Failures);
        }

        var consumerOptions = new RuoyuJwtBearerConsumerOptions();
        configureConsumer?.Invoke(consumerOptions);

        // Exact match only: wildcards, self-reported headers and role-only
        // bypasses are not accepted anywhere on this path.
        var adminAudiences = identityOptions.GetAdminAudiences();

        services.AddAuthentication()
            .AddJwtBearer(RuoyuAdminApiAuthorization.SchemeName, options =>
            {
                options.Authority = identityOptions.Authority.Trim().TrimEnd('/');
                options.RequireHttpsMetadata = identityOptions.RequireHttpsMetadata;
                options.MapInboundClaims = consumerOptions.MapInboundClaims;
                options.TokenValidationParameters = IdentityTokenValidationParametersFactory.Create(
                    identityOptions,
                    nameClaimType: consumerOptions.NameClaimType,
                    roleClaimType: consumerOptions.RoleClaimType,
                    // No administrator audience means unconfigured: reject every token.
                    audienceOverride: adminAudiences.Count == 0 ? string.Empty : adminAudiences[0]);
                if (adminAudiences.Count > 1)
                {
                    // Several exact application ids (the Portal consoles, ADR-0030).
                    options.TokenValidationParameters.ValidAudience = null;
                    options.TokenValidationParameters.ValidAudiences = adminAudiences;
                }
            });

        return services;
    }
}
