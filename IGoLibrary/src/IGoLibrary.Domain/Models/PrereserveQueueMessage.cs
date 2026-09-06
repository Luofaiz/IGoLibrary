namespace IGoLibrary.Domain.Models;

public sealed record PrereserveQueueMessage(
    string? Namespace,
    string Message,
    int? Code,
    int? Data,
    string RawPayload)
{
    private const string SuccessPrefix = "你已经成功登记了明天的";
    private const string QueueReadyPrefix = "排队成功";
    private const string UserInfoFailedMessage = "获取用户信息失败，请尝试重新进入此页面";

    public bool IndicatesSuccess =>
        Message.StartsWith(SuccessPrefix, StringComparison.Ordinal) ||
        Message.Contains("已经预定了座位", StringComparison.Ordinal) ||
        Message.Contains("成功登记", StringComparison.Ordinal) ||
        Message.Contains("已登记", StringComparison.Ordinal);

    public bool IndicatesQueueReady =>
        Message.StartsWith(QueueReadyPrefix, StringComparison.Ordinal) ||
        Message.Contains("不需要排队", StringComparison.Ordinal);

    public bool IndicatesCookieInvalid => Message == "1000" || Code == 1000;

    public bool RequestsSessionRefresh =>
        Code == 0 && Data == 1 ||
        string.Equals(Message, UserInfoFailedMessage, StringComparison.Ordinal);
}
