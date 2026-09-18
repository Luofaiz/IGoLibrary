using System.Text.Json;
using IGoLibrary.Domain.Models;
using IGoLibrary.Infrastructure.Api;

namespace IGoLibrary.Tests;

public sealed class TomorrowProtocolTests
{
    [Fact]
    public async Task VenueWarmup_UsesOneSmallTargetedQuery_AndQueueHttpProfile()
    {
        var handler = new SequenceHttpMessageHandler(async (request, token) =>
        {
            Assert.Equal(PrereserveQueueClient.TomorrowReservationUserAgent, request.Headers.UserAgent.ToString());
            Assert.Equal(PrereserveQueueClient.TomorrowReservationAppVersion, Assert.Single(request.Headers.GetValues("App-Version")));
            Assert.Equal(PrereserveQueueClient.QueueOrigin, Assert.Single(request.Headers.GetValues("Origin")));
            Assert.Equal("https://web.traceint.com/", request.Headers.Referrer!.AbsoluteUri);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("libLayout", payload.RootElement.GetProperty("operationName").GetString());
            Assert.Equal(117580, payload.RootElement.GetProperty("variables").GetProperty("libId").GetInt32());
            var query = payload.RootElement.GetProperty("query").GetString()!;
            Assert.Contains("prereserve { libLayout(libId: $libId)", query);
            Assert.Contains("seats_booking seats_total seats_used", query);
            Assert.DoesNotContain("seat_key", query);
            Assert.DoesNotContain("seats {", query);
            return await SequenceHttpMessageHandler.JsonResponseAsync("""{"data":{"userAuth":{"prereserve":{"libLayout":{"seats_total":10,"seats_used":1,"seats_booking":0}}}}}""");
        });
        var client = new TraceIntApiClient(new HttpClient(handler),
            new FakeProtocolTemplateStore(new ProtocolTemplateSet("https://example.com", "{}", "{}", "{}", "{}", "{}", "{}")),
            new FakeSettingsService(AppSettings.Default));

        await client.WarmUpPrereserveLibraryAsync("Authorization=test", 117580);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task TodayTransport_KeepsOriginalProfile()
    {
        var handler = new SequenceHttpMessageHandler((request, _) =>
        {
            Assert.Equal("2.0.11", Assert.Single(request.Headers.GetValues("App-Version")));
            Assert.Contains("WindowsWechat(0x63070626)", request.Headers.UserAgent.ToString());
            Assert.Equal("https://web.traceint.com/web/index.html", request.Headers.Referrer!.AbsoluteUri);
            return SequenceHttpMessageHandler.JsonResponseAsync("{}");
        });
        var transport = new TraceIntGraphQlTransport(new HttpClient(handler), new TraceIntRequestPolicy(new FakeSettingsService(AppSettings.Default)));
        using var response = await transport.SendAsync("Authorization=test", "{}", usePrereserveHeaders: false);
    }
}
