using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace IGoLibrary.Infrastructure.Api;

public sealed class TraceIntGraphQlTransport(HttpClient httpClient, TraceIntRequestPolicy requestPolicy)
{
    private const string DesktopUserAgent = "Mozilla/5.0 (Windows NT 6.1; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/81.0.4044.138 Safari/537.36 NetType/WIFI MicroMessenger/7.0.20.1781(0x6700143B) WindowsWechat(0x63070626)";
    private const string MobileWechatUserAgent = "Mozilla/5.0 (Linux; Android 10; TAS-AL00 Build/HUAWEITAS-AL00; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/107.0.5304.141 Mobile Safari/537.36 XWEB/5043 MMWEBSDK/20221109 MMWEBID/6856 MicroMessenger/8.0.31.2281(0x28001F59) WeChat/arm64 Weixin NetType/WIFI Language/zh_CN ABI/arm64";

    public Task<HttpResponseMessage> SendAsync(string cookie, string payload, bool usePrereserveHeaders, CancellationToken cancellationToken = default)
    {
        return requestPolicy.ExecuteAsync(async requestToken =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://wechat.v2.traceint.com/index.php/graphql/")
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
            request.Headers.Host = "wechat.v2.traceint.com";
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            request.Headers.TryAddWithoutValidation("Connection", "keep-alive");
            request.Headers.TryAddWithoutValidation("Origin", "https://web.traceint.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://web.traceint.com/web/index.html");
            request.Headers.TryAddWithoutValidation("User-Agent", usePrereserveHeaders ? MobileWechatUserAgent : DesktopUserAgent);
            request.Headers.TryAddWithoutValidation("App-Version", usePrereserveHeaders ? "2.0.14" : "2.0.11");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
            request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en-US;q=0.8,en;q=0.7");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
            request.Headers.ExpectContinue = false;
            var payloadBytes = Encoding.UTF8.GetBytes(payload);
            request.Content = new ByteArrayContent(payloadBytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content.Headers.ContentLength = payloadBytes.Length;

            var response = await httpClient.SendAsync(request, requestToken);
            response.EnsureSuccessStatusCode();
            return response;
        }, cancellationToken);
    }
}
