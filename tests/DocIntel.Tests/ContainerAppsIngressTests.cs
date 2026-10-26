using System.Net;
using System.Text;
using DocIntel.Api.Http;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class ContainerAppsIngressTests
{
    [Fact]
    public async Task Production_Ingress_Issues_Secure_Csrf_Cookies_And_Validates_A_Multipart_Upload()
    {
        // Arrange
        using var services = Services();
        var pipeline = Pipeline(services);
        var csrf = Context(services);

        // Act
        await pipeline(csrf);
        var cookie = csrf.Response.Headers.SetCookie.ToString();
        var upload = Upload(services, cookie.Split(';')[0], Assert.IsType<string>(csrf.Items["token"]));
        await pipeline(upload);

        // Assert
        Assert.True(csrf.Request.IsHttps);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("sample.pdf", upload.Items["uploaded"]);
        Assert.True(upload.Request.IsHttps);
    }

    [Fact]
    public async Task Invalid_Csrf_Tokens_Are_Still_Rejected_Before_The_Upload_Is_Read()
    {
        // Arrange
        using var services = Services();
        var pipeline = Pipeline(services);
        var csrf = Context(services);
        await pipeline(csrf);
        var upload = Upload(services, csrf.Response.Headers.SetCookie.ToString().Split(';')[0], "invalid-token");

        // Act / Assert
        await Assert.ThrowsAsync<AntiforgeryValidationException>(() => pipeline(upload));
        Assert.False(upload.Items.ContainsKey("uploaded"));
    }

    [Theory]
    [InlineData("non-aca")]
    [InlineData("development")]
    [InlineData("wrong-host")]
    [InlineData("wrong-port")]
    [InlineData("http")]
    [InlineData("missing")]
    [InlineData("chain")]
    [InlineData("duplicate")]
    public async Task Untrusted_Or_Ambiguous_Headers_Do_Not_Upgrade_The_Request_Scheme(string scenario)
    {
        // Arrange
        using var services = Services();
        var settings = Settings();
        if (scenario == "non-aca") settings.Remove("CONTAINER_APP_NAME");
        var context = Context(services);
        switch (scenario)
        {
            case "wrong-host": context.Request.Host = new HostString("untrusted.example"); break;
            case "wrong-port": context.Request.Host = new HostString("app.example", 8080); break;
            case "http": context.Request.Headers["X-Forwarded-Proto"] = "http"; break;
            case "missing": context.Request.Headers.Remove("X-Forwarded-Proto"); break;
            case "chain": context.Request.Headers["X-Forwarded-Proto"] = "https, https"; break;
            case "duplicate": context.Request.Headers["X-Forwarded-Proto"] = new StringValues(["https", "https"]); break;
        }
        var pipeline = Pipeline(services, settings, scenario == "development" ? "Development" : "Production",
            validateCsrf: false);

        // Act
        await pipeline(context);

        // Assert
        Assert.False(context.Request.IsHttps);
    }

    [Fact]
    public async Task Forwarded_Client_Address_Host_And_Prefix_Cannot_Change_Authorization_Inputs()
    {
        // Arrange
        using var services = Services();
        var context = Context(services);
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.20.30.40");
        context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        context.Request.Headers["X-Forwarded-Host"] = "attacker.example";
        context.Request.Headers["X-Forwarded-Prefix"] = "/other";
        var pipeline = Pipeline(services, validateCsrf: false);

        // Act
        await pipeline(context);

        // Assert
        Assert.True(context.Request.IsHttps);
        Assert.Equal(IPAddress.Parse("10.20.30.40"), context.Connection.RemoteIpAddress);
        Assert.Equal("app.example", context.Request.Host.Host);
        Assert.Equal(PathString.Empty, context.Request.PathBase);
    }

    [Theory]
    [InlineData("CONTAINER_APP_ENV_DNS_SUFFIX", "untrusted.example")]
    [InlineData("CONTAINER_APP_NAME", "invalid app")]
    [InlineData("Identity:PublicOrigin", "http://app.example")]
    [InlineData("Identity:PublicOrigin", "https://user@app.example")]
    [InlineData("Identity:PublicOrigin", "https://app.example/path")]
    [InlineData("Identity:PublicOrigin", "https://app.example?query=value")]
    public void Invalid_Container_App_Proxy_Configuration_Fails_Fast(string setting, string value)
    {
        // Arrange
        using var services = Services();
        var settings = Settings();
        settings[setting] = value;

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => Pipeline(services, settings));
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-DocIntel-CSRF";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
        });
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> Settings() => new()
    {
        ["CONTAINER_APP_NAME"] = "docintel",
        ["CONTAINER_APP_ENV_DNS_SUFFIX"] = "environment.westus.azurecontainerapps.io",
        ["Identity:PublicOrigin"] = "https://app.example"
    };

    private static RequestDelegate Pipeline(ServiceProvider services,
        Dictionary<string, string?>? settings = null, string environment = "Production", bool validateCsrf = true)
    {
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(value => value.EnvironmentName).Returns(environment);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? Settings()).Build();
        var app = new ApplicationBuilder(services);
        app.UseDocIntelIngress(configuration, host.Object);
        app.Run(async context =>
        {
            if (!validateCsrf) return;
            var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
            if (HttpMethods.IsGet(context.Request.Method))
                context.Items["token"] = antiforgery.GetAndStoreTokens(context).RequestToken;
            else
            {
                await antiforgery.ValidateRequestAsync(context);
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                context.Items["uploaded"] = Assert.Single(form.Files).FileName;
            }
        });
        return app.Build();
    }

    private static DefaultHttpContext Context(ServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "GET";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("app.example");
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static DefaultHttpContext Upload(ServiceProvider services, string cookie, string token)
    {
        var context = Context(services);
        context.Request.Method = "POST";
        context.Request.Headers.Cookie = cookie;
        context.Request.Headers["X-DocIntel-CSRF"] = token;
        context.Request.ContentType = "multipart/form-data; boundary=test-boundary";
        var body = Encoding.UTF8.GetBytes(
            "--test-boundary\r\nContent-Disposition: form-data; name=\"file\"; filename=\"sample.pdf\"\r\n" +
            "Content-Type: application/pdf\r\n\r\n%PDF-1.7 test data\r\n--test-boundary--\r\n");
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        return context;
    }
}
