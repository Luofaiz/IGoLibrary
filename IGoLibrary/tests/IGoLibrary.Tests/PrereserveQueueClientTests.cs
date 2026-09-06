using IGoLibrary.Infrastructure.Api;

namespace IGoLibrary.Tests;

public sealed class PrereserveQueueClientTests
{
    [Fact]
    public void TransportSettings_UseTomorrowReservationQueueProfile()
    {
        Assert.StartsWith("wss://", PrereserveQueueClient.QueueUri, StringComparison.Ordinal);
        Assert.Equal("https://web.traceint.com", PrereserveQueueClient.QueueOrigin);
        Assert.Contains("WindowsWechat", PrereserveQueueClient.TomorrowReservationUserAgent);
        Assert.InRange(PrereserveQueueClient.KeepAliveInterval.TotalMilliseconds, 200, 300);
    }

    [Fact]
    public void ParseMessage_DetectsQueueHandshake()
    {
        var message = PrereserveQueueClient.ParseMessage("""{"ns":"prereserve/queue","msg":"","code":0,"data":1}""");

        Assert.True(message.RequestsSessionRefresh);
        Assert.False(message.IndicatesQueueReady);
        Assert.False(message.IndicatesSuccess);
    }

    [Fact]
    public void ParseMessage_DetectsQueueReadyMessage()
    {
        var message = PrereserveQueueClient.ParseMessage("""{"ns":"prereserve/queue","msg":"排队成功！请在2分钟内选择座位，否则需要重新排队。","code":0,"data":0}""");

        Assert.True(message.IndicatesQueueReady);
        Assert.False(message.RequestsSessionRefresh);
        Assert.False(message.IndicatesSuccess);
    }

    [Fact]
    public void ParseMessage_DetectsNoQueueNeededAsQueueReady()
    {
        var message = PrereserveQueueClient.ParseMessage("""{"ns":"prereserve/queue","msg":"不需要排队，可以直接预约。","code":0,"data":0}""");

        Assert.True(message.IndicatesQueueReady);
        Assert.False(message.RequestsSessionRefresh);
        Assert.False(message.IndicatesSuccess);
    }

    [Fact]
    public void ParseMessage_DetectsTomorrowReservationSuccess()
    {
        var message = PrereserveQueueClient.ParseMessage("""{"ns":"prereserve/queue","msg":"你已经成功登记了明天的 自科阅览区 225","code":0,"data":0}""");

        Assert.True(message.IndicatesSuccess);
        Assert.False(message.RequestsSessionRefresh);
    }

    [Fact]
    public void ParseMessage_DetectsExistingTomorrowReservationSuccess()
    {
        var message = PrereserveQueueClient.ParseMessage("""{"ns":"prereserve/queue","msg":"您已经预定了座位：自科阅览区 210","code":0,"data":0}""");

        Assert.True(message.IndicatesSuccess);
        Assert.False(message.RequestsSessionRefresh);
    }
}
