using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Emenda.Host.Security;

public static class ProductCallerAuthenticationExtensions
{
    public const string RuntimeModeKey = "Emenda:Runtime:Mode";
    public const string StandaloneMode = "Standalone";

    public static bool IsStandaloneProductMode(this IConfiguration configuration) =>
        string.Equals(
            configuration[RuntimeModeKey],
            StandaloneMode,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Registers product caller authentication and fails startup when a
    /// standalone deployment has no caller credential and anonymous access is
    /// not explicitly allowed for local development.
    /// </summary>
    public static IServiceCollection AddProductCallerAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(ProductCallerAuthenticationOptions.SectionName);
        services.Configure<ProductCallerAuthenticationOptions>(section);

        var options = section.Get<ProductCallerAuthenticationOptions>()
            ?? new ProductCallerAuthenticationOptions();
        if (options.Enabled && !options.AllowAnonymous && options.ValidCallers.Count == 0)
        {
            throw new InvalidOperationException(
                "Standalone mode requires at least one caller credential under " +
                $"{ProductCallerAuthenticationOptions.SectionName}:Callers (Name + ApiKey). " +
                "Set " +
                $"{ProductCallerAuthenticationOptions.SectionName}:AllowAnonymous=true only for local development.");
        }

        if (options.AllowAnonymous)
        {
            Console.WriteLine(
                "WARNING: Emenda caller authentication is disabled " +
                $"({ProductCallerAuthenticationOptions.SectionName}:AllowAnonymous=true). " +
                "Never expose this deployment to untrusted clients.");
        }

        return services;
    }
}