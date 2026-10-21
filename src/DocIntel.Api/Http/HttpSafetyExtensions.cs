namespace DocIntel.Api.Http;

/// <summary>Response policies apply even to exception responses; same-origin PDFs remain embeddable.</summary>
public static class HttpSafetyExtensions
{
    public const long MaximumRequestBodyBytes = 26 * 1024 * 1024;

    public static void UseDocIntelResponseHeaders(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["X-DocIntel-Correlation-Id"] = context.TraceIdentifier;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
                if (!app.Environment.IsDevelopment())
                    headers["Strict-Transport-Security"] = "max-age=31536000";
                var pdf = context.Response.ContentType?.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase) == true;
                headers["X-Frame-Options"] = pdf ? "SAMEORIGIN" : "DENY";
                if (pdf)
                {
                    headers["Content-Security-Policy"] = "frame-ancestors 'self'; base-uri 'none'; form-action 'none'";
                }
                else
                {
                    var path = context.Request.Path;
                    var legacy = path == "/" || path == "/index.html" || path == "/admin" || path == "/admin.html";
                    var review = path == "/review" || path == "/review.html";
                    var script = legacy ? "'self' 'unsafe-inline'" :
                        review ? "'self' https://cdnjs.cloudflare.com" : "'self'";
                    var style = legacy
                        ? "'self' 'unsafe-inline' https://fonts.googleapis.com https://fonts.cdnfonts.com" : "'self'";
                    headers["Content-Security-Policy"] =
                        $"default-src 'self'; script-src {script}; style-src {style}; " +
                        "font-src 'self' https://fonts.gstatic.com https://fonts.cdnfonts.com; " +
                        "img-src 'self' data: blob:; connect-src 'self'; frame-src 'self' blob:; " +
                        (review ? "worker-src 'self' blob: https://cdnjs.cloudflare.com; " : "worker-src 'self'; ") +
                        "frame-ancestors 'none'; base-uri 'self'; object-src 'none'; form-action 'self'";
                }
                if (context.Response.StatusCode >= 400 ||
                    context.Request.Path.StartsWithSegments("/api") ||
                    context.Request.Path.StartsWithSegments("/auth") ||
                    context.Request.Path.StartsWithSegments("/signin-oidc"))
                    headers.CacheControl = "no-store";
                return Task.CompletedTask;
            });
            await next();
        });
    }
}
