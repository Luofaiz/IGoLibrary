using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using IGoLibrary.Application.Abstractions;
using IGoLibrary.Domain.Models;

namespace IGoLibrary.Infrastructure.Api;

public sealed class PrereserveQueueClient : IPrereserveQueueClient
{
    private const string QueueUri = "ws://wechat.v2.traceint.com/ws?ns=prereserve/queue";
    private const string ClientPayload = """{"ns":"prereserve/queue","msg":""}""";
    private const string MobileWechatUserAgent = "Mozilla/5.0 (Linux; Android 10; TAS-AL00 Build/HUAWEITAS-AL00; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/107.0.5304.141 Mobile Safari/537.36 XWEB/5043 MMWEBSDK/20221109 MMWEBID/6856 MicroMessenger/8.0.31.2281(0x28001F59) WeChat/arm64 Weixin NetType/WIFI Language/zh_CN ABI/arm64";

    public async Task RunAsync(
        string cookie,
        Func<PrereserveQueueMessage, CancellationToken, Task> onMessageAsync,
        CancellationToken cancellationToken = default)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("User-Agent", MobileWechatUserAgent);
        socket.Options.SetRequestHeader("App-Version", "2.0.14");
        socket.Options.SetRequestHeader("Cookie", cookie);

        await socket.ConnectAsync(new Uri(QueueUri), cancellationToken);

        using var keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var keepAliveTask = SendKeepAliveAsync(socket, keepAliveCts.Token);

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var raw = await ReceiveTextMessageAsync(socket, cancellationToken);
                if (raw is null)
                {
                    break;
                }

                var message = ParseMessage(raw);
                await onMessageAsync(message, cancellationToken);
            }
        }
        finally
        {
            keepAliveCts.Cancel();
            try
            {
                await keepAliveTask;
            }
            catch (OperationCanceledException)
            {
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", closeCts.Token);
                }
                catch (WebSocketException)
                {
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    internal static PrereserveQueueMessage ParseMessage(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            return new PrereserveQueueMessage(
                ReadOptionalString(root, "ns"),
                ReadMessage(root),
                ReadOptionalInt(root, "code"),
                ReadOptionalInt(root, "data"),
                raw);
        }
        catch (JsonException)
        {
            return new PrereserveQueueMessage(null, raw, null, null, raw);
        }
    }

    private static async Task SendKeepAliveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (socket.State != WebSocketState.Open)
            {
                return;
            }

            var payload = Encoding.UTF8.GetBytes(ClientPayload);
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(800), cancellationToken);
        }
    }

    private static async Task<string?> ReceiveTextMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private static string ReadMessage(JsonElement root)
    {
        if (!root.TryGetProperty("msg", out var message))
        {
            return string.Empty;
        }

        return message.ValueKind switch
        {
            JsonValueKind.String => message.GetString() ?? string.Empty,
            JsonValueKind.Number => message.ToString(),
            _ => message.ToString()
        };
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int? ReadOptionalInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }
}
