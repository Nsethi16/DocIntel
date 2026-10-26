using DocIntel.Api.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace DocIntel.Api.Http;

/// <summary>Restores the ingress-verified HTTPS scheme without trusting forwarded client identities or hosts.</summary>
public static class ContainerAppsIngressExtensions
{
    /// <summary>Processes only the protocol header overwritten by Azure Container Apps HTTP ingress.</summary>
    public static IApplicationBuilder UseDocIntelIngress(
        this IApplicationBuilder app, IConfiguration configuration, IHostEnvironment environment)
    {
        var appName = configuration["CONTAINER_APP_NAME"];
        var suffix = configuration["CONTAINER_APP_ENV_DNS_SUFFIX"];
        if (environment.IsDevelopment() || String.IsNullOrWhiteSpace(appName) || String.IsNullOrWhiteSpace(suffix))
            return app;
        if (!suffix.EndsWith(".azurecontainerapps.io", StringComparison.OrdinalIgnoreCase) ||
            Uri.CheckHostName(appName + "." + suffix) != UriHostNameType.Dns ||
            !Uri.TryCreate(configuration[$"{IdentityOptions.SectionName}:PublicOrigin"], UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || !String.IsNullOrEmpty(origin.UserInfo) ||
            !String.IsNullOrEmpty(origin.Query) || !String.IsNullOrEmpty(origin.Fragment) ||
            origin.AbsolutePath != "/" || origin.Port != 443)
            throw new InvalidOperationException("Container Apps ingress requires valid hosting metadata and an HTTPS public origin.");

        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1
        };
        // ACA overwrites X-Forwarded-Proto at HTTP ingress; proxy addresses can change between revisions.
        // Trust only that header in ACA, for the configured host, never X-Forwarded-For/Host or direct TCP ingress.
        forwarded.KnownIPNetworks.Clear();
        forwarded.KnownProxies.Clear();
        app.UseWhen(context =>
            String.Equals(context.Request.Host.Host, origin.Host, StringComparison.OrdinalIgnoreCase) &&
            (context.Request.Host.Port is null or 443) &&
            context.Request.Headers["X-Forwarded-Proto"].Count == 1 &&
            String.Equals(context.Request.Headers["X-Forwarded-Proto"][0], "https", StringComparison.OrdinalIgnoreCase),
            branch => branch.UseForwardedHeaders(forwarded));
        return app;
    }
}
