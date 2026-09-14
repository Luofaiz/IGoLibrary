using IGoLibrary.Infrastructure.Api;
using IGoLibrary.Domain.Models;

namespace IGoLibrary.Tests;

public sealed class CancellationConfirmationTests
{
    private static TraceIntApiClient Create(params string[] responses) => new(
        new HttpClient(new SequenceHttpMessageHandler(responses.Select<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>(
            json => (_, _) => SequenceHttpMessageHandler.JsonResponseAsync(json)).ToArray())),
        new FakeProtocolTemplateStore(new ProtocolTemplateSet("{}", "{}", "{}", "{}", "{}", "{}", "{}")),
        new FakeSettingsService(AppSettings.Default with { RetryCount = 0 }));

    [Theory]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("\"未成功\"")]
    public async Task CancelToday_DoesNotAcceptNegativeResult(string result)
    {
        var api = Create("{\"data\":{\"userAuth\":{\"reserve\":{\"reserveCancle\":" + result + "}}}}");
        Assert.False(await api.CancelReservationAsync("cookie", "token"));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("{\"hours\":1,\"mins\":20}")]
    public async Task CancelToday_RequiresExplicitAbsenceAfterAcknowledgement(string result)
    {
        var acknowledgement = "{\"data\":{\"userAuth\":{\"reserve\":{\"reserveCancle\":" + result + "}}}}";
        var gone = Create(acknowledgement, """{"data":{"userAuth":{"reserve":{"reserve":null}}}}""");
        Assert.True(await gone.CancelReservationAsync("cookie", "token"));
        var stillPresent = Create(acknowledgement, """{"data":{"userAuth":{"reserve":{"reserve":{"seat_key":"A"}}}}}""");
        Assert.False(await stillPresent.CancelReservationAsync("cookie", "token"));
    }

    [Fact]
    public async Task CancelToday_MalformedConfirmationIsNotSuccess()
    {
        var api = Create("""{"data":{"userAuth":{"reserve":{"reserveCancle":true}}}}""", "{}");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => api.CancelReservationAsync("cookie", "token"));
    }

    [Fact]
    public async Task CancelToday_NegatedSuccessErrorIsNotSuccess()
    {
        var api = Create("""{"errors":[{"msg":"取消未成功，请重试","code":1}]}""");
        await Assert.ThrowsAsync<IGoLibrary.Application.Exceptions.TraceIntApiException>(() => api.CancelReservationAsync("cookie", "token"));
    }

    [Theory]
    [InlineData("null", true)]
    [InlineData("[]", true)]
    [InlineData("[{\"id\":1,\"is_used\":false}]", false)]
    public async Task CancelTomorrow_ConfirmsRemoval(string records, bool expected)
    {
        var api = Create("""{"data":{"userAuth":{"prereserve":{"cancle":true}}}}""",
            "{\"data\":{\"userAuth\":{\"prereserve\":{\"prereserve\":" + records + "}}}}");
        Assert.Equal(expected, await api.CancelPrereserveAsync("cookie"));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("\"取消未成功\"")]
    public async Task CancelTomorrow_RejectsNegativeResult(string result)
    {
        var api = Create("{\"data\":{\"userAuth\":{\"prereserve\":{\"cancle\":" + result + "}}}}");
        Assert.False(await api.CancelPrereserveAsync("cookie"));
    }
}
