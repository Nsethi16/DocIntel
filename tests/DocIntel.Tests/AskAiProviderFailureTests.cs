using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using DocIntel.Core.Features.AskAi;
using Moq;
using Xunit;

namespace DocIntel.Tests;

public sealed class AskAiProviderFailureTests
{
    [Theory]
    [InlineData(400, "ProviderRequestRejected")]
    [InlineData(401, "ProviderAccessDenied")]
    [InlineData(403, "ProviderAccessDenied")]
    [InlineData(404, "ProviderDeploymentUnavailable")]
    [InlineData(429, "ProviderRateLimited")]
    [InlineData(503, "ProviderUnavailable")]
    [InlineData(504, "ProviderTimedOut")]
    public void Actual_Provider_HTTP_Status_Is_Not_Confused_With_The_Exception_HResult(int status, string code)
    {
        // Arrange
        var exception = Failure(status, """{"error":{"code":"unknown","message":"PRIVATE_PROVIDER_DETAIL"}}""");

        // Act
        var details = AskAiProviderFailure.FromException(exception);

        // Assert
        Assert.Equal(status, details!.HttpStatus);
        Assert.Equal(code, AskAiFailureDiagnostics.GetCode(exception));
        Assert.DoesNotContain("PRIVATE_PROVIDER_DETAIL", JsonSerializer.Serialize(details));
        Assert.DoesNotContain("PRIVATE_PROVIDER_DETAIL", AskAiFailureDiagnostics.GetMessage(code));
    }

    [Fact]
    public void Schema_Diagnostics_Expose_Only_Allowlisted_Codes_And_Internal_Parameter_Prefixes()
    {
        // Arrange
        var exception = Failure(400, """
            {"error":{"code":"invalid_function_parameters",
            "param":"tools[3].function.parameters.PRIVATE_CUSTOMER",
            "message":"PRIVATE_DOCUMENT_VALUE: additionalProperties must be false"}}
            """);

        // Act
        var details = AskAiProviderFailure.FromException(exception);

        // Assert
        Assert.Equal("ProviderSchemaRejected", details!.FailureCode);
        Assert.Equal("invalid_function_parameters", details.Code);
        Assert.Equal("tools[3].function.parameters", details.Parameter);
        Assert.Equal("additionalProperties", details.SchemaKeyword);
        Assert.Equal("Response", details.Origin);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(details));
    }

    [Theory]
    [InlineData("context_length_exceeded", "ProviderContextLimit")]
    [InlineData("insufficient_quota", "ProviderQuotaExceeded")]
    [InlineData("content_filter", "ProviderContentFiltered")]
    public void Known_Provider_Rejections_Have_Actionable_And_Fixed_Messages(string providerCode, string failureCode)
    {
        // Arrange
        var exception = Failure(400, JsonSerializer.Serialize(new
            { error = new { code = providerCode, message = "PRIVATE_CONTENT" } }));

        // Act
        var code = AskAiFailureDiagnostics.GetCode(exception);

        // Assert
        Assert.Equal(failureCode, code);
        Assert.DoesNotContain("PRIVATE_CONTENT", AskAiFailureDiagnostics.GetMessage(code));
    }

    [Fact]
    public void Unknown_Or_Malformed_Error_Bodies_Do_Not_Leak_Content_Or_Mask_The_Provider_Failure()
    {
        // Arrange
        var malformed = Failure(429, "PRIVATE_INVALID_JSON");
        var unknown = Failure(400, """{"error":{"code":"PRIVATE_CODE","param":"PRIVATE_PARAM","message":"PRIVATE"}}""");

        // Act
        var first = AskAiProviderFailure.FromException(malformed);
        var second = AskAiProviderFailure.FromException(new InvalidOperationException("Wrapper", unknown));

        // Assert
        Assert.Equal("ProviderRateLimited", first!.FailureCode);
        Assert.Equal("MessageOnly", first.Origin);
        Assert.Equal("Unknown", second!.Code);
        Assert.Null(second.Parameter);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(second));
    }

    private static ClientResultException Failure(int status, string json)
    {
        var response = new Mock<PipelineResponse>();
        response.SetupGet(value => value.Status).Returns(status);
        response.SetupGet(value => value.Content).Returns(BinaryData.FromString(json));
        return new ClientResultException("Service request failed.", response.Object);
    }
}
