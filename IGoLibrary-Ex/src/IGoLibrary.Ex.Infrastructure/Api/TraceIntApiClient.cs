using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IGoLibrary.Ex.Application.Abstractions;
using IGoLibrary.Ex.Application.Exceptions;
using IGoLibrary.Ex.Application.Services;
using IGoLibrary.Ex.Application.State;
using IGoLibrary.Ex.Domain.Enums;
using IGoLibrary.Ex.Domain.Models;
using IGoLibrary.Ex.Domain.Helpers;
using RestSharp;

namespace IGoLibrary.Ex.Infrastructure.Api;

public sealed class TraceIntApiClient(
    HttpClient httpClient,
    IProtocolTemplateStore protocolTemplateStore,
    ISettingsService settingsService,
    AppRuntimeState? runtimeState = null,
    ICredentialStore? credentialStore = null,
    IActivityLogService? activityLogService = null) : ITraceIntApiClient
{
    private const string DesktopUserAgent = "Mozilla/5.0 (Windows NT 6.1; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/81.0.4044.138 Safari/537.36 NetType/WIFI MicroMessenger/7.0.20.1781(0x6700143B) WindowsWechat(0x63070626)";
    private const string MobileWechatUserAgent = "Mozilla/5.0 (Linux; Android 10; TAS-AL00 Build/HUAWEITAS-AL00; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/107.0.5304.141 Mobile Safari/537.36 XWEB/5043 MMWEBSDK/20221109 MMWEBID/6856 MicroMessenger/8.0.31.2281(0x28001F59) WeChat/arm64 Weixin NetType/WIFI Language/zh_CN ABI/arm64";
    private const string AppVersion = "2.0.11";
    private const string PrereserveAppVersion = "2.0.14";
    private static readonly TimeSpan GetCookieTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _cookieUpdateGate = new(1, 1);

    public async Task<string> GetCookieFromCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var templates = await protocolTemplateStore.GetEffectiveTemplatesAsync(cancellationToken);
        var requestUrl = templates.GetCookieUrlTemplate.Replace("ReplaceMeByCode", code, StringComparison.Ordinal);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(GetCookieTimeout);

        using var client = new RestClient(requestUrl);
        var request = new RestRequest
        {
            Method = Method.Get
        };

        RestResponse response;
        try
        {
            response = await client.ExecuteAsync(request, timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"获取 Cookie 超时（{GetCookieTimeout.TotalSeconds:0} 秒），请检查网络或稍后重试。", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("获取 Cookie 请求失败，请检查网络连接或授权链接是否可访问。", ex);
        }

        var responseCookies = response.Cookies?.Select(cookie => cookie.ToString()).ToArray();
        ThrowIfCookieResponseFailed(response, responseCookies);
        return BuildCookieHeaderFromResponseCookies(responseCookies);
    }

    public async Task ValidateCookieAsync(string cookie, CancellationToken cancellationToken = default)
    {
        _ = await GetLibrariesAsync(cookie, cancellationToken);
    }

    public async Task<IReadOnlyList<LibrarySummary>> GetLibrariesAsync(string cookie, CancellationToken cancellationToken = default)
    {
        var templates = await protocolTemplateStore.GetEffectiveTemplatesAsync(cancellationToken);
        using var response = await SendGraphQlAsync(cookie, templates.QueryLibrariesTemplate, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(raw);

        ThrowIfGraphQlError(document.RootElement);
        var libs = document.RootElement
            .GetProperty("data")
            .GetProperty("userAuth")
            .GetProperty("reserve")
            .GetProperty("libs");

        var results = new List<LibrarySummary>();
        foreach (var item in libs.EnumerateArray())
        {
            var floor = item.GetProperty("lib_floor").GetString() ?? string.Empty;
            if (floor == "0")
            {
                continue;
            }

            var runtime = item.TryGetProperty("lib_rt", out var runtimeElement)
                ? runtimeElement
                : default;

            results.Add(new LibrarySummary(
                item.GetProperty("lib_id").GetInt32(),
                item.GetProperty("lib_name").GetString() ?? "Unknown",
                floor,
                item.GetProperty("is_open").GetBoolean(),
                ReadOptionalIntProperty(runtime, "seats_total"),
                ReadOptionalIntProperty(runtime, "seats_used"),
                ReadOptionalIntProperty(runtime, "seats_booking")));
        }

        return results;
    }

    public async Task<string?> GetCurrentUserNicknameAsync(string cookie, CancellationToken cancellationToken = default)
    {
        var templates = await protocolTemplateStore.GetEffectiveTemplatesAsync(cancellationToken);
        using var response = await SendGraphQlAsync(cookie, templates.QueryReservationInfoTemplate, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(raw);

        ThrowIfGraphQlError(document.RootElement);
        var currentUser = document.RootElement
            .GetProperty("data")
            .GetProperty("userAuth")
            .GetProperty("currentUser");

        if (currentUser.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            !currentUser.TryGetProperty("user_nick", out var nicknameElement) ||
            nicknameElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var nickname = nicknameElement.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(nickname) ? null : nickname;
    }

    public async Task<LibraryLayout> GetLibraryLayoutAsync(string cookie, int libraryId, CancellationToken cancellationToken = default)
    {
        var templates = await protocolTemplateStore.GetEffectiveTemplatesAsync(cancellationToken);
        var payload = templates.QueryLibraryLayoutTemplate.Replace("ReplaceMe", libraryId.ToString(), StringComparison.Ordinal);
        using var response = await SendGraphQlAsync(cookie, payload, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(raw);

        ThrowIfGraphQlError(document.RootElement);
        var lib = document.RootElement
            .GetProperty("data")
            .GetProperty("userAuth")
            .GetProperty("reserve")
            .GetProperty("libs")[0];

        return ParseLibraryLayout(lib, lib.GetProperty("lib_layout"));
    }

    public async Task<LibraryLayout> GetPrereserveLibraryLayoutAsync(string cookie, int libraryId, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            operationName = "libLayout",
            query = "query libLayout($libId: Int!) {\n  userAuth {\n    prereserve {\n      libLayout(libId: $libId) {\n        max_x\n        max_y\n        seats_booking\n        seats_total\n        seats_used\n        seats {\n          key\n          name\n          seat_status\n          status\n          type\n          x\n          y\n        }\n      }\n    }\n  }\n}",
            variables = new
            {
                libId = libraryId
            }
        });

        using var response = await SendGraphQlAsync(cookie, payload, cancellationToken, usePrereserveHeaders: true);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(raw);

        ThrowIfGraphQlError(document.RootElement);
        var layout = document.RootElement
            .GetProperty("data")
            .GetProperty("userAuth")
            .GetProperty("prereserve")
            .GetProperty("libLayout");

        return ParseLibraryLayout(libraryId, "明日预约场馆", string.Empty, true, layout);
    }

    private static LibraryLayout ParseLibraryLayout(JsonElement lib, JsonElement layout)
    {
        return ParseLibraryLayout(
            lib.GetProperty("lib_id").GetInt32(),
            lib.GetProperty("lib_name").GetString() ?? "Unknown",
            lib.GetProperty("lib_floor").GetString() ?? string.Empty,
            lib.GetProperty("is_open").GetBoolean(),
            layout);
    }

    private static LibraryLayout ParseLibraryLayout(
        int libraryId,
        string libraryName,
        string libraryFloor,
        bool isOpen,
        JsonElement layout)
    {
        var seats = new List<SeatSnapshot>();
        foreach (var seat in layout.GetProperty("seats").EnumerateArray())
        {
            if (!IsSeatLayoutItem(seat))
            {
                continue;
            }

            var key = ReadOptionalStringProperty(seat, "key").Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!TryReadBooleanLikeProperty(seat, "status", out var isOccupied) ||
                !TryReadRequiredIntProperty(seat, "x", out var x) ||
                !TryReadRequiredIntProperty(seat, "y", out var y))
            {
                continue;
            }

            var name = ReadOptionalStringProperty(seat, "name").Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = key;
            }

            seats.Add(new SeatSnapshot(
                key,
                name,
                isOccupied,
                x,
                y));
        }

        return new LibraryLayout(
            libraryId,
            libraryName,
            libraryFloor,
            isOpen,
            ReadOptionalIntProperty(layout, "seats_total"),
            ReadOptionalIntProperty(layout, "seats_booking"),
            ReadOptionalIntProperty(layout, "seats_used"),
            seats.OrderBy(x => int.TryParse(x.SeatName, out var number) ? number : int.MaxValue).ToList());
    }

    public async Task<LibraryRule> GetLibraryRuleAsync(string cookie, int libraryId, CancellationToken cancellationToken = default)
    {
        var templates = await protocolTemplateStore.GetEffectiveTemplatesAsync(cancellationToken);
        var payload = templates.QueryLibraryRuleTemplate.Replace("ReplaceMe", libraryId.ToString(), StringComparison.Ordinal);
        using var response = await SendGraphQlAsync(cookie, payload, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(raw);

        ThrowIfGraphQlError(document.RootElement);
        var rule = document.RootElement
            .GetProperty("data")
            .GetProperty("userAuth")
            .GetProperty("reserve")
            .GetProperty("libRule");

        return new LibraryRule(
            libraryId,
            rule.GetProperty("advance_booking").GetString() ?? string.Empty,
            rule.GetProperty("lib_seat_ttl").GetString() ?? string.Empty,
            rule.GetProperty("lib_hold_ttl").GetString() ?? string.Empty,
            rule.GetProperty("lib_renew_time").GetString() ?? string.Empty,
            rule.GetProperty("hold_reason").GetString() ?? string.Empty,
            rule.TryGetProperty("close_start_date", out var closeStartDate) && closeStartDate.ValueKind != JsonValueKind.Null
                ? closeStartDate.GetString()
                : null,
            rule.TryGetProperty("close_end_date", out var closeEndDate) && closeEndDate.ValueKind != JsonValueKind.Null
                ? closeEndDate.GetString()
                : null,
            rule.GetProperty("open_time").GetInt64(),
            rule.GetProperty("open_time_str").GetString() ?? string.Empty,
            rule.GetProperty("close_time").GetInt64(),
            rule.GetProperty("close_time_str").GetString() ?? string.Empty,
            rule.GetProperty("lib_validate_time").GetInt32());
    }

    public async Task<ReservationInfo?> GetReservationInfoAsync(string cookie, CancellationToken cancellationToken = default)
    {
        var templates = await protocolTemplateStore.GetEffectiveTemplatesAsync(cancellationToken);
        using var response = await SendGraphQlAsync(cookie, templates.QueryReservationInfoTemplate, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(raw);

        ThrowIfGraphQlError(document.RootElement);
        if (!TryReadTodayReservationRecord(document.RootElement, out var today) || today.ExpirationTime is null)
        {
            return null;
        }

        return new ReservationInfo(
            today.ReservationToken,
            today.LibraryId,
            today.LibraryName,
            today.SeatKey,
            today.SeatName,
            today.ExpirationTime.Value,
            today.IsCheckedIn);
    }

    public async Task<IReadOnlyList<ReservationRecord>> GetReservationRecordsAsync(string cookie, CancellationToken cancellationToken = default)
    {
        var templates = await protocolTemplateStore.GetEffectiveTemplatesAsync(cancellationToken);
        using var todayResponse = await SendGraphQlAsync(cookie, templates.QueryReservationInfoTemplate, cancellationToken);
        var todayRaw = await todayResponse.Content.ReadAsStringAsync(cancellationToken);
        using var todayDocument = JsonDocument.Parse(todayRaw);

        ThrowIfGraphQlError(todayDocument.RootElement);
        var records = new List<ReservationRecord>();
        if (TryReadTodayReservationRecord(todayDocument.RootElement, out var today))
        {
            records.Add(today);
        }

        var tomorrowRecords = await GetTomorrowReservationRecordsAsync(cookie, cancellationToken);
        records.AddRange(tomorrowRecords);

        return records
            .GroupBy(record => (record.Kind, record.LibraryId, record.SeatKey, record.ReservationDate))
            .Select(group => group.First())
            .OrderBy(record => record.Kind)
            .ThenBy(record => record.LibraryName, StringComparer.Ordinal)
            .ThenBy(record => record.SeatName, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<bool> ReserveSeatAsync(string cookie, int libraryId, string seatKey, CancellationToken cancellationToken = default)
    {
        var templates = await protocolTemplateStore.GetEffectiveTemplatesAsync(cancellationToken);
        var payload = templates.ReserveSeatTemplate
            .Replace("ReplaceMeBySeatKey", seatKey, StringComparison.Ordinal)
            .Replace("ReplaceMeByLibID", libraryId.ToString(), StringComparison.Ordinal);

        using var response = await SendGraphQlAsync(cookie, payload, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(raw);

        ThrowIfGraphQlError(document.RootElement);
        var reserveResult = document.RootElement
            .GetProperty("data")
            .GetProperty("userAuth")
            .GetProperty("reserve")
            .GetProperty("reserueSeat");

        return ReadBooleanLike(reserveResult, "reserueSeat");
    }

    public async Task RefreshPrereservePageAsync(string cookie, CancellationToken cancellationToken = default)
    {
        var prereservePayload = """{"operationName":"prereserve","query":"query prereserve {\n userAuth {\n prereserve {\n prereserve {\n day\n lib_id\n seat_key\n seat_name\n is_used\n user_mobile\n id\n lib_name\n }\n }\n }\n}"}""";
        var indexPayload = """{"operationName":"index","query":"query index {\n userAuth {\n user {\n prereserveAuto: getSchConfig(extra: true, fields: \"prereserve.auto\")\n }\n currentUser {\n sch {\n isShowCommon\n }\n }\n prereserve {\n libs {\n is_open\n lib_floor\n lib_group_id\n lib_id\n lib_name\n num\n seats_total\n }\n }\n oftenseat {\n prereserveList {\n id\n info\n lib_id\n seat_key\n status\n }\n }\n }\n}"}""";

        using var prereserveResponse = await SendGraphQlAsync(cookie, prereservePayload, cancellationToken, usePrereserveHeaders: true);
        var prereserveRaw = await prereserveResponse.Content.ReadAsStringAsync(cancellationToken);
        using var prereserveDocument = JsonDocument.Parse(prereserveRaw);
        ThrowIfGraphQlError(prereserveDocument.RootElement);

        using var indexResponse = await SendGraphQlAsync(cookie, indexPayload, cancellationToken, usePrereserveHeaders: true);
        var indexRaw = await indexResponse.Content.ReadAsStringAsync(cancellationToken);
        using var indexDocument = JsonDocument.Parse(indexRaw);
        ThrowIfGraphQlError(indexDocument.RootElement);
    }

    public async Task<PrereserveSaveResult> SavePrereserveSeatAsync(
        string cookie,
        int libraryId,
        string seatKey,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            operationName = "save",
            query = "mutation save($key: String!, $libid: Int!, $captchaCode: String, $captcha: String) {\n userAuth {\n prereserve {\n save(key: $key, libId: $libid, captcha: $captcha, captchaCode: $captchaCode)\n }\n }\n}",
            variables = new
            {
                key = $"{seatKey}.",
                libid = libraryId,
                captchaCode = string.Empty,
                captcha = string.Empty
            }
        });

        using var response = await SendGraphQlAsync(cookie, payload, cancellationToken, usePrereserveHeaders: true);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(raw);
        var updatedCookie = ApplyResponseCookies(cookie, response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToArray()
            : null);

        if (TryFindErrorInfo(document.RootElement, out var errorInfo))
        {
            throw new TraceIntApiException(errorInfo.Message, errorInfo.Code, errorInfo.Message);
        }

        var submitted = document.RootElement.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("userAuth", out var userAuth) &&
                        userAuth.ValueKind is not JsonValueKind.Null;

        return new PrereserveSaveResult(
            submitted,
            updatedCookie,
            submitted ? "明日预约请求已提交。" : "明日预约接口未返回授权信息。");
    }

    public async Task<bool> CancelReservationAsync(string cookie, string reservationToken, CancellationToken cancellationToken = default)
    {
        var templates = await protocolTemplateStore.GetEffectiveTemplatesAsync(cancellationToken);
        var payload = templates.CancelReservationTemplate.Replace("ReplaceMe", reservationToken, StringComparison.Ordinal);

        using var response = await SendGraphQlAsync(cookie, payload, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(raw);

        if (TryFindErrorInfo(document.RootElement, out var errorInfo))
        {
            return errorInfo.Message.Contains("成功", StringComparison.OrdinalIgnoreCase);
        }

        if (document.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("userAuth", out var userAuth) &&
            userAuth.TryGetProperty("reserve", out var reserve) &&
            reserve.TryGetProperty("reserveCancle", out _))
        {
            return true;
        }

        return false;
    }

    public async Task<bool> CancelPrereserveAsync(string cookie, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            operationName = "cancle",
            query = "mutation cancle {\n userAuth {\n prereserve {\n cancle\n }\n }\n}",
            variables = new { }
        });

        using var response = await SendGraphQlAsync(cookie, payload, cancellationToken, usePrereserveHeaders: true);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(raw);

        if (TryFindErrorInfo(document.RootElement, out var errorInfo))
        {
            return errorInfo.Message.Contains("成功", StringComparison.OrdinalIgnoreCase);
        }

        if (document.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("userAuth", out var userAuth) &&
            userAuth.TryGetProperty("prereserve", out var prereserve) &&
            prereserve.TryGetProperty("cancle", out var cancleResult))
        {
            if (cancleResult.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return false;
            }

            try
            {
                return ReadBooleanLike(cancleResult, "cancle");
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<HttpResponseMessage> SendGraphQlAsync(
        string cookie,
        string payload,
        CancellationToken cancellationToken,
        bool usePrereserveHeaders = false)
    {
        return await ExecuteWithRequestPolicyAsync(async requestToken =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://wechat.v2.traceint.com/index.php/graphql/");
            request.Version = HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            request.Headers.Host = "wechat.v2.traceint.com";
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            request.Headers.TryAddWithoutValidation("Connection", "keep-alive");
            request.Headers.TryAddWithoutValidation("Origin", "https://web.traceint.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://web.traceint.com/web/index.html");
            request.Headers.TryAddWithoutValidation("User-Agent", usePrereserveHeaders ? MobileWechatUserAgent : DesktopUserAgent);
            request.Headers.TryAddWithoutValidation("App-Version", usePrereserveHeaders ? PrereserveAppVersion : AppVersion);
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
            await TryUpdateSessionCookieFromResponseAsync(cookie, response, cancellationToken);
            return response;
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<ReservationRecord>> GetTomorrowReservationRecordsAsync(
        string cookie,
        CancellationToken cancellationToken)
    {
        const string prereservePayload = """{"operationName":"prereserve","query":"query prereserve {\n userAuth {\n prereserve {\n prereserve {\n day\n lib_id\n seat_key\n seat_name\n is_used\n user_mobile\n id\n lib_name\n }\n }\n }\n}"}""";

        using var response = await SendGraphQlAsync(cookie, prereservePayload, cancellationToken, usePrereserveHeaders: true);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(raw);

        ThrowIfGraphQlError(document.RootElement);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("userAuth", out var userAuth) ||
            !userAuth.TryGetProperty("prereserve", out var prereserveNode) ||
            !prereserveNode.TryGetProperty("prereserve", out var prereserve) ||
            prereserve.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        var records = new List<ReservationRecord>();
        if (prereserve.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in prereserve.EnumerateArray())
            {
                if (TryReadTomorrowReservationRecord(item, out var record))
                {
                    records.Add(record);
                }
            }
        }
        else if (TryReadTomorrowReservationRecord(prereserve, out var record))
        {
            records.Add(record);
        }

        return records;
    }

    private static bool TryReadTodayReservationRecord(JsonElement root, out ReservationRecord record)
    {
        record = default!;
        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("userAuth", out var userAuth) ||
            !userAuth.TryGetProperty("reserve", out var reserveNode) ||
            !reserveNode.TryGetProperty("reserve", out var reservation) ||
            reservation.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        if (!reservation.TryGetProperty("exp_date", out var expirationElement) ||
            !expirationElement.TryGetInt64(out var expirationTimestamp))
        {
            return false;
        }

        var expirationTime = ReservationTimeHelper.FromUnixSeconds(expirationTimestamp);
        record = new ReservationRecord(
            ReservationRecordKind.Today,
            reserveNode.TryGetProperty("getSToken", out var tokenElement)
                ? tokenElement.GetString() ?? string.Empty
                : ReadOptionalStringProperty(reservation, "token"),
            ReadOptionalIntProperty(reservation, "lib_id"),
            ReadOptionalStringProperty(reservation, "lib_name"),
            ReadOptionalStringProperty(reservation, "seat_key"),
            ReadOptionalStringProperty(reservation, "seat_name"),
            expirationTime,
            ResolveTodayReservationDate(reservation, expirationTime),
            IsCheckedIn: IsTodayReservationCheckedIn(reservation));
        return true;
    }

    private static bool TryReadTomorrowReservationRecord(JsonElement item, out ReservationRecord record)
    {
        record = default!;
        if (item.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }

        var seatKey = ReadOptionalStringProperty(item, "seat_key");
        var seatName = ReadOptionalStringProperty(item, "seat_name");
        var libraryName = ReadOptionalStringProperty(item, "lib_name");
        if (string.IsNullOrWhiteSpace(seatKey) && string.IsNullOrWhiteSpace(seatName))
        {
            return false;
        }

        record = new ReservationRecord(
            ReservationRecordKind.Tomorrow,
            ReadOptionalStringProperty(item, "id"),
            ReadOptionalIntProperty(item, "lib_id"),
            libraryName,
            seatKey,
            seatName,
            null,
            ResolvePrereserveDate(item),
            ReadOptionalBooleanProperty(item, "is_used"));
        return true;
    }

    private static bool IsTodayReservationCheckedIn(JsonElement reservation)
    {
        if (ReadOptionalUnixTimestampProperty(reservation, "validate_date") is not null)
        {
            return true;
        }

        var holdDate = ReadOptionalStringProperty(reservation, "hold_date");
        if (!string.IsNullOrWhiteSpace(holdDate) && holdDate.Trim() != "0")
        {
            return true;
        }

        return false;
    }

    private static DateOnly? ResolveTodayReservationDate(JsonElement reservation, DateTimeOffset expirationTime)
    {
        var dateText = ReadOptionalStringProperty(reservation, "date");
        if (TryParseDateOnly(dateText, out var date))
        {
            return date;
        }

        return DateOnly.FromDateTime(expirationTime.LocalDateTime.Date);
    }

    private static DateOnly? ResolvePrereserveDate(JsonElement item)
    {
        if (!item.TryGetProperty("day", out var day) || day.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return GetFallbackPrereserveDate();
        }

        if (day.ValueKind == JsonValueKind.Number && day.TryGetInt64(out var numericValue))
        {
            return ResolvePrereserveDateFromNumber(numericValue) ?? GetFallbackPrereserveDate();
        }

        if (day.ValueKind == JsonValueKind.String)
        {
            var text = day.GetString();
            if (long.TryParse(text, out var parsedNumber))
            {
                return ResolvePrereserveDateFromNumber(parsedNumber) ?? GetFallbackPrereserveDate();
            }

            if (TryParseDateOnly(text, out var parsedDate))
            {
                return parsedDate;
            }
        }

        return GetFallbackPrereserveDate();
    }

    private static DateOnly GetFallbackPrereserveDate()
    {
        return DateOnly.FromDateTime(DateTime.Now.Date.AddDays(1));
    }

    private static DateOnly? ResolvePrereserveDateFromNumber(long value)
    {
        if (TryParseCompactDate(value, out var compactDate))
        {
            return compactDate;
        }

        if (TryParseUnixDate(value, out var unixDate))
        {
            return unixDate;
        }

        if (value is >= 0 and <= 31)
        {
            return DateOnly.FromDateTime(DateTime.Now.Date.AddDays(Math.Max(1, value)));
        }

        return null;
    }

    private static bool TryParseCompactDate(long value, out DateOnly date)
    {
        date = default;
        if (value is < 10_001_01 or > 99_991_231)
        {
            return false;
        }

        var year = (int)(value / 10_000);
        var month = (int)(value / 100 % 100);
        var day = (int)(value % 100);
        if (year is < 1 or > 9999)
        {
            return false;
        }

        try
        {
            date = new DateOnly(year, month, day);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryParseUnixDate(long value, out DateOnly date)
    {
        date = default;
        var seconds = value > 9_999_999_999 ? value / 1000 : value;
        if (seconds is < -62_135_596_800 or > 253_402_300_799)
        {
            return false;
        }

        try
        {
            date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().DateTime);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryParseDateOnly(string? text, out DateOnly date)
    {
        if (DateOnly.TryParse(text, out date))
        {
            return true;
        }

        if (DateTime.TryParse(text, out var dateTime))
        {
            date = DateOnly.FromDateTime(dateTime);
            return true;
        }

        return false;
    }

    private async Task TryUpdateSessionCookieFromResponseAsync(
        string requestCookie,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (runtimeState is null || credentialStore is null)
        {
            return;
        }

        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return;
        }

        var responseCookies = values.ToArray();

        await _cookieUpdateGate.WaitAsync(cancellationToken);
        try
        {
            var currentSession = runtimeState.Session;
            if (currentSession is null)
            {
                return;
            }

            if (!TryGetCookieValue(requestCookie, "Authorization", out var requestAuthorization) ||
                !TryGetCookieValue(currentSession.Cookie, "Authorization", out var currentAuthorization) ||
                !string.Equals(requestAuthorization, currentAuthorization, StringComparison.Ordinal))
            {
                return;
            }

            var updatedCookie = ApplyResponseCookies(currentSession.Cookie, responseCookies);
            if (string.Equals(updatedCookie, currentSession.Cookie, StringComparison.Ordinal))
            {
                return;
            }

            var updatedSession = currentSession with
            {
                Cookie = updatedCookie,
                SavedAt = DateTimeOffset.Now
            };

            runtimeState.Session = updatedSession;
            if (updatedSession.CanAutoRestore)
            {
                await credentialStore.SaveSessionAsync(updatedSession, cancellationToken);
            }

            if (CookieExpiryDetector.TryGetExpirationTime(updatedCookie, out var expirationTime))
            {
                activityLogService?.Write(LogEntryKind.Info, "Auth", $"服务端返回新的 Authorization，已自动更新 Cookie。新到期时间：{expirationTime:yyyy-MM-dd HH:mm:ss}。");
            }
            else
            {
                activityLogService?.Write(LogEntryKind.Info, "Auth", "服务端返回新的 Authorization，已自动更新 Cookie。");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activityLogService?.Write(LogEntryKind.Warning, "Auth", $"自动更新 Cookie 失败：{ex.Message}");
        }
        finally
        {
            _cookieUpdateGate.Release();
        }
    }

    private async Task<T> ExecuteWithRequestPolicyAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var settings = await LoadNetworkSettingsAsync(cancellationToken);
        Exception? lastException = null;

        for (var attempt = 0; attempt <= settings.RetryCount; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(settings.Timeout);

            try
            {
                return await operation(timeoutCts.Token);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                lastException = new TimeoutException($"请求超时（>{settings.Timeout.TotalSeconds:0} 秒）。", ex);
            }
            catch (HttpRequestException ex) when (IsTransient(ex.StatusCode))
            {
                lastException = ex;
            }

            if (attempt >= settings.RetryCount)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken);
        }

        throw lastException ?? new InvalidOperationException("请求失败。");
    }

    private async Task<(TimeSpan Timeout, int RetryCount)> LoadNetworkSettingsAsync(CancellationToken cancellationToken)
    {
        AppSettings settings;
        try
        {
            settings = await settingsService.LoadAsync(cancellationToken);
        }
        catch
        {
            settings = AppSettings.Default;
        }

        var timeoutSeconds = Math.Clamp(settings.ApiTimeoutSeconds, 1, 60);
        var retryCount = Math.Clamp(settings.RetryCount, 0, 10);
        return (TimeSpan.FromSeconds(timeoutSeconds), retryCount);
    }

    private static bool IsTransient(HttpStatusCode? statusCode)
    {
        return statusCode is null
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            || (int?)statusCode >= 500;
    }

    private static void ThrowIfGraphQlError(JsonElement root)
    {
        if (TryGetAuthorizationDeniedError(root, out var authError))
        {
            throw new TraceIntApiException(
                authError.Message,
                authError.Code,
                authError.Message,
                isAuthorizationDenied: true);
        }

        if (TryFindErrorInfo(root, out var errorInfo))
        {
            throw new TraceIntApiException(errorInfo.Message, errorInfo.Code, errorInfo.Message);
        }
    }

    private static bool TryGetAuthorizationDeniedError(JsonElement root, out GraphQlErrorInfo errorInfo)
    {
        errorInfo = default;
        if (root.ValueKind is not JsonValueKind.Object ||
            !root.TryGetProperty("errors", out var errors) ||
            errors.ValueKind is not JsonValueKind.Array ||
            errors.GetArrayLength() == 0 ||
            !TryReadErrorInfo(errors[0], out var candidate) ||
            candidate.Code != 40001 ||
            !IsAccessDeniedMessage(candidate.Message))
        {
            return false;
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }

        if (!data.TryGetProperty("userAuth", out var userAuth) || userAuth.ValueKind is not JsonValueKind.Null)
        {
            return false;
        }

        errorInfo = candidate;
        return true;
    }

    private static bool TryFindErrorInfo(JsonElement element, out GraphQlErrorInfo errorInfo)
    {
        if (TryReadErrorInfo(element, out errorInfo))
        {
            return true;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (TryFindErrorInfo(property.Value, out errorInfo))
                    {
                        return true;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindErrorInfo(item, out errorInfo))
                    {
                        return true;
                    }
                }

                break;
        }

        errorInfo = default;
        return false;
    }

    private static bool TryReadErrorInfo(JsonElement element, out GraphQlErrorInfo errorInfo)
    {
        errorInfo = default;
        if (element.ValueKind is not JsonValueKind.Object ||
            !element.TryGetProperty("msg", out var messageElement) ||
            messageElement.ValueKind is not JsonValueKind.String)
        {
            return false;
        }

        var message = messageElement.GetString();
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        int? code = null;
        if (element.TryGetProperty("code", out var codeElement))
        {
            code = codeElement.ValueKind switch
            {
                JsonValueKind.Number when codeElement.TryGetInt32(out var intValue) => intValue,
                JsonValueKind.String when int.TryParse(codeElement.GetString(), out var intValue) => intValue,
                _ => null
            };
        }

        errorInfo = new GraphQlErrorInfo(message, code);
        return true;
    }

    private static bool IsAccessDeniedMessage(string message)
    {
        return string.Equals(message.Trim(), "access denied!", StringComparison.OrdinalIgnoreCase)
            || string.Equals(message.Trim(), "access denied", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct GraphQlErrorInfo(string Message, int? Code);

    private static bool ReadBooleanLike(JsonElement element, string fieldName)
    {
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => string.Equals(element.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Number when element.TryGetInt32(out var intValue) => intValue != 0,
            _ => throw new InvalidOperationException($"字段 {fieldName} 的返回类型不受支持: {element.ValueKind}")
        };
    }

    private static bool IsSeatLayoutItem(JsonElement element)
    {
        if (element.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }

        if (!element.TryGetProperty("type", out _))
        {
            return true;
        }

        return TryReadRequiredIntProperty(element, "type", out var type) && type == 1;
    }

    private static bool TryReadBooleanLikeProperty(JsonElement element, string propertyName, out bool value)
    {
        value = default;
        if (element.ValueKind is not JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        try
        {
            value = ReadBooleanLike(property, propertyName);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryReadRequiredIntProperty(JsonElement element, string propertyName, out int value)
    {
        value = default;
        if (element.ValueKind is not JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out value) => true,
            JsonValueKind.String when int.TryParse(property.GetString(), out value) => true,
            _ => false
        };
    }

    private static string ReadOptionalStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind is not JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null)
        {
            return string.Empty;
        }

        return property.ValueKind is JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : property.ToString();
    }

    private static int ReadOptionalIntProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind is not JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var intValue) => intValue,
            JsonValueKind.String when int.TryParse(property.GetString(), out var intValue) => intValue,
            _ => 0
        };
    }

    private static long? ReadOptionalUnixTimestampProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind is not JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        long value;
        if (property.ValueKind == JsonValueKind.Number)
        {
            if (!property.TryGetInt64(out value))
            {
                return null;
            }
        }
        else if (property.ValueKind == JsonValueKind.String)
        {
            if (!long.TryParse(property.GetString(), out value))
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        return value > 0 ? value : null;
    }

    private static bool ReadOptionalBooleanProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind is not JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        try
        {
            return ReadBooleanLike(property, propertyName);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static string BuildCookieHeaderFromResponseCookies(IReadOnlyList<string>? responseCookies)
    {
        if (responseCookies is null)
        {
            throw new InvalidOperationException("响应报文返回的Cookie为空");
        }

        if (responseCookies.Count < 2)
        {
            throw new InvalidOperationException("Cookie不包含关键身份信息，可能是code过期，重新填写含code的链接");
        }

        return $"{responseCookies[1]}; {responseCookies[0]}";
    }

    internal static string ApplyResponseCookies(string cookie, IReadOnlyList<string>? responseCookies)
    {
        if (responseCookies is null || responseCookies.Count == 0)
        {
            return cookie;
        }

        var cookieParts = cookie
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        foreach (var responseCookie in responseCookies)
        {
            var cookiePair = responseCookie.Split(';', 2, StringSplitOptions.TrimEntries)[0];
            var equalsIndex = cookiePair.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            var cookieName = cookiePair[..equalsIndex];
            var existingIndex = cookieParts.FindIndex(part =>
            {
                var partEqualsIndex = part.IndexOf('=');
                return partEqualsIndex > 0 &&
                       string.Equals(part[..partEqualsIndex], cookieName, StringComparison.OrdinalIgnoreCase);
            });

            if (existingIndex >= 0)
            {
                cookieParts[existingIndex] = cookiePair;
            }
            else
            {
                cookieParts.Add(cookiePair);
            }
        }

        return string.Join("; ", cookieParts);
    }

    internal static bool ContainsCookieName(IReadOnlyList<string> responseCookies, string cookieName)
    {
        return responseCookies.Any(cookie =>
        {
            var cookiePair = cookie.Split(';', 2, StringSplitOptions.TrimEntries)[0];
            var equalsIndex = cookiePair.IndexOf('=');
            return equalsIndex > 0 &&
                   string.Equals(cookiePair[..equalsIndex], cookieName, StringComparison.OrdinalIgnoreCase);
        });
    }

    internal static bool TryGetCookieValue(string cookie, string cookieName, out string value)
    {
        value = string.Empty;
        foreach (var part in cookie.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsIndex = part.IndexOf('=');
            if (equalsIndex <= 0 ||
                !string.Equals(part[..equalsIndex], cookieName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = part[(equalsIndex + 1)..];
            return true;
        }

        return false;
    }

    internal static void ThrowIfCookieResponseFailed(RestResponse response, IReadOnlyList<string>? responseCookies)
    {
        if (response.IsSuccessful || responseCookies?.Count >= 2)
        {
            return;
        }

        var reason = response.ErrorMessage;
        if (string.IsNullOrWhiteSpace(reason))
        {
            reason = response.StatusDescription;
        }

        if (string.IsNullOrWhiteSpace(reason) && response.ResponseStatus != ResponseStatus.Completed)
        {
            reason = response.ResponseStatus.ToString();
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            reason = "请检查授权链接是否过期或网络是否可用";
        }

        if (response.StatusCode is not 0)
        {
            throw new HttpRequestException(
                $"获取 Cookie 请求失败，HTTP {(int)response.StatusCode} {response.StatusCode}：{reason}",
                response.ErrorException,
                response.StatusCode);
        }

        throw new InvalidOperationException($"获取 Cookie 请求失败：{reason}", response.ErrorException);
    }
}
