using System.Text.Json;
using IGoLibrary.Application.Abstractions;
using IGoLibrary.Application.State;
using IGoLibrary.Domain.Enums;
using IGoLibrary.Domain.Models;

namespace IGoLibrary.Application.Services;

public sealed class SessionService(
    ITraceIntApiClient apiClient,
    ICredentialStore credentialStore,
    IActivityLogService activityLogService,
    AppRuntimeState runtimeState) : ISessionService
{
    public SessionCredentials? CurrentSession => runtimeState.Session;

    public async Task<SessionCredentials> AuthenticateFromCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var cookie = await apiClient.GetCookieFromCodeAsync(code, cancellationToken);
        await ValidateCookieForSessionAsync(cookie, cancellationToken);

        var session = new SessionCredentials(cookie, SessionSource.QrCodeLink, DateTimeOffset.Now, true);
        runtimeState.Session = session;
        await credentialStore.SaveSessionAsync(session, cancellationToken);
        activityLogService.Write(LogEntryKind.Success, "Auth", "通过扫码链接获取并验证 Cookie 成功。");
        return session;
    }

    public async Task<SessionCredentials> AuthenticateFromCookieAsync(string cookie, bool remember, CancellationToken cancellationToken = default)
    {
        var pendingSession = new SessionCredentials(cookie, SessionSource.ManualCookie, DateTimeOffset.Now, remember);
        runtimeState.Session = pendingSession;
        try
        {
            await ValidateCookieForSessionAsync(cookie, cancellationToken);
        }
        catch
        {
            runtimeState.Session = null;
            throw;
        }

        var session = runtimeState.Session ?? pendingSession;
        runtimeState.Session = session;
        if (remember)
        {
            await credentialStore.SaveSessionAsync(session, cancellationToken);
        }
        else
        {
            await ClearStoredSessionSafelyAsync("用户选择不记住本次会话，已清理本地持久化会话。", cancellationToken);
        }

        activityLogService.Write(LogEntryKind.Success, "Auth", "手动 Cookie 验证成功。");
        return session;
    }

    public async Task<SessionCredentials?> RestoreAsync(CancellationToken cancellationToken = default)
    {
        SessionCredentials? stored;
        try
        {
            stored = await credentialStore.LoadSessionAsync(cancellationToken);
        }
        catch (JsonException)
        {
            await ClearStoredSessionSafelyAsync("本地持久化会话已损坏，已自动清理。", cancellationToken);
            return null;
        }

        if (stored is null)
        {
            activityLogService.Write(LogEntryKind.Info, "Auth", "没有可恢复的会话。");
            return null;
        }

        try
        {
            runtimeState.Session = stored;
            await ValidateCookieForSessionAsync(stored.Cookie, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            runtimeState.Session = null;
            await ClearStoredSessionSafelyAsync($"本地会话已失效，已自动移除：{ex.Message}", cancellationToken);
            return null;
        }
        catch
        {
            runtimeState.Session = null;
            throw;
        }

        var restored = (runtimeState.Session ?? stored) with { Source = SessionSource.Restored };
        runtimeState.Session = restored;
        activityLogService.Write(LogEntryKind.Success, "Auth", "已恢复本地保存的会话。");
        return restored;
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        runtimeState.Session = null;
        runtimeState.BoundLibrary = null;
        runtimeState.CurrentLayout = null;
        runtimeState.CurrentReservation = null;
        runtimeState.ReservationRecords = [];
        runtimeState.Libraries = [];
        await credentialStore.ClearSessionAsync(cancellationToken);
        activityLogService.Write(LogEntryKind.Info, "Auth", "已清除当前会话。");
    }

    private async Task ValidateCookieForSessionAsync(string cookie, CancellationToken cancellationToken)
    {
        var wasExpired = CookieExpiryDetector.TryGetExpirationTime(cookie, out var expirationTime) &&
                         expirationTime <= DateTimeOffset.Now;
        if (wasExpired)
        {
            activityLogService.Write(LogEntryKind.Info, "Auth", "本地 Cookie 已达到 JWT 到期时间，正在尝试通过服务端会话刷新身份 Cookie。");
        }
        else if (CookieExpiryDetector.TryGetExpirationTime(cookie, out expirationTime))
        {
            activityLogService.Write(LogEntryKind.Info, "Auth", $"Cookie JWT 未过期，到期时间：{expirationTime:yyyy-MM-dd HH:mm:ss}。");
        }

        await apiClient.ValidateCookieAsync(cookie, cancellationToken);

        if (wasExpired)
        {
            var refreshedCookie = runtimeState.Session?.Cookie;
            if (string.Equals(refreshedCookie, cookie, StringComparison.Ordinal) ||
                (CookieExpiryDetector.TryGetExpirationTime(refreshedCookie, out var refreshedExpiration) &&
                 refreshedExpiration <= DateTimeOffset.Now))
            {
                throw new InvalidOperationException(CookieExpiryDetector.BuildExpiredMessage(expirationTime));
            }

            activityLogService.Write(LogEntryKind.Success, "Auth", "服务端已接受过期 Cookie 并返回可继续使用的会话 Cookie。");
        }
    }

    private async Task ClearStoredSessionSafelyAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            await credentialStore.ClearSessionAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            activityLogService.Write(LogEntryKind.Warning, "Auth", $"清理本地会话失败：{ex.Message}");
            return;
        }

        activityLogService.Write(LogEntryKind.Warning, "Auth", message);
    }
}
