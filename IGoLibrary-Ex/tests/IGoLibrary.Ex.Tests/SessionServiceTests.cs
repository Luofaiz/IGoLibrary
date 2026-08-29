using System.Net;
using System.Text;
using System.Text.Json;
using IGoLibrary.Ex.Application.Services;
using IGoLibrary.Ex.Application.State;
using IGoLibrary.Ex.Domain.Enums;
using IGoLibrary.Ex.Domain.Models;

namespace IGoLibrary.Ex.Tests;

public sealed class SessionServiceTests
{
    [Fact]
    public async Task AuthenticateFromCookieAsync_ValidatesApi_WhenAuthorizationJwtIsPresent()
    {
        var validateCalls = 0;
        var apiClient = new FakeTraceIntApiClient
        {
            OnValidateCookieAsync = (_, _) =>
            {
                validateCalls++;
                return Task.CompletedTask;
            }
        };
        var credentialStore = new FakeCredentialStore();
        var runtimeState = new AppRuntimeState();
        var service = new SessionService(apiClient, credentialStore, new ActivityLogService(), runtimeState);
        var cookie = BuildAuthorizationCookie(DateTimeOffset.Now.AddMinutes(5));

        var session = await service.AuthenticateFromCookieAsync(cookie, remember: true);

        Assert.Equal(cookie, session.Cookie);
        Assert.Equal(1, validateCalls);
        Assert.Equal(1, credentialStore.SaveCalls);
    }

    [Fact]
    public async Task AuthenticateFromCookieAsync_SavesCookieUpdatedDuringValidation()
    {
        var runtimeState = new AppRuntimeState();
        var apiClient = new FakeTraceIntApiClient
        {
            OnValidateCookieAsync = (_, _) =>
            {
                runtimeState.Session = runtimeState.Session! with { Cookie = "Authorization=new-token; SERVERID=new-server" };
                return Task.CompletedTask;
            }
        };
        var credentialStore = new FakeCredentialStore();
        var service = new SessionService(apiClient, credentialStore, new ActivityLogService(), runtimeState);

        var session = await service.AuthenticateFromCookieAsync("Authorization=old-token; SERVERID=old-server", remember: true);

        Assert.Equal("Authorization=new-token; SERVERID=new-server", session.Cookie);
        Assert.Equal(session, runtimeState.Session);
        Assert.Equal(session, credentialStore.StoredSession);
    }

    [Fact]
    public async Task AuthenticateFromCookieAsync_DoesNotSaveSession_WhenFutureJwtFailsApiValidation()
    {
        var apiClient = new FakeTraceIntApiClient
        {
            OnValidateCookieAsync = (_, _) => Task.FromException(new InvalidOperationException("Cookie 无效"))
        };
        var credentialStore = new FakeCredentialStore();
        var runtimeState = new AppRuntimeState();
        var service = new SessionService(apiClient, credentialStore, new ActivityLogService(), runtimeState);
        var cookie = BuildAuthorizationCookie(DateTimeOffset.Now.AddMinutes(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AuthenticateFromCookieAsync(cookie, remember: true));

        Assert.Null(runtimeState.Session);
        Assert.Equal(0, credentialStore.SaveCalls);
        Assert.Null(credentialStore.StoredSession);
    }

    [Fact]
    public async Task AuthenticateFromCookieAsync_ClearsStoredSession_WhenRememberDisabled()
    {
        var apiClient = new FakeTraceIntApiClient();
        var credentialStore = new FakeCredentialStore
        {
            StoredSession = new SessionCredentials("old-cookie", SessionSource.ManualCookie, DateTimeOffset.Now.AddDays(-1), true)
        };
        var runtimeState = new AppRuntimeState();
        var service = new SessionService(apiClient, credentialStore, new ActivityLogService(), runtimeState);

        var session = await service.AuthenticateFromCookieAsync("fresh-cookie", remember: false);

        Assert.Equal("fresh-cookie", session.Cookie);
        Assert.Equal(session, runtimeState.Session);
        Assert.Equal(0, credentialStore.SaveCalls);
        Assert.Equal(1, credentialStore.ClearCalls);
        Assert.Null(credentialStore.StoredSession);
    }

    [Fact]
    public async Task RestoreAsync_ClearsStoredSession_WhenExpiredJwtCannotBeRefreshed()
    {
        var validateCalls = 0;
        var apiClient = new FakeTraceIntApiClient
        {
            OnValidateCookieAsync = (_, _) =>
            {
                validateCalls++;
                return Task.CompletedTask;
            }
        };
        var credentialStore = new FakeCredentialStore
        {
            StoredSession = new SessionCredentials(
                BuildAuthorizationCookie(DateTimeOffset.Now.AddSeconds(-1)),
                SessionSource.ManualCookie,
                DateTimeOffset.Now.AddDays(-1),
                true)
        };
        var runtimeState = new AppRuntimeState();
        var service = new SessionService(apiClient, credentialStore, new ActivityLogService(), runtimeState);

        var restored = await service.RestoreAsync();

        Assert.Null(restored);
        Assert.Null(runtimeState.Session);
        Assert.Equal(1, credentialStore.ClearCalls);
        Assert.Equal(1, validateCalls);
    }

    [Fact]
    public async Task RestoreAsync_AcceptsExpiredJwt_WhenServerReturnsRefreshedCookie()
    {
        var runtimeState = new AppRuntimeState();
        var expiredCookie = BuildAuthorizationCookie(DateTimeOffset.Now.AddSeconds(-1));
        var refreshedCookie = BuildAuthorizationCookie(DateTimeOffset.Now.AddHours(2));
        var apiClient = new FakeTraceIntApiClient
        {
            OnValidateCookieAsync = (_, _) =>
            {
                runtimeState.Session = runtimeState.Session! with { Cookie = refreshedCookie };
                return Task.CompletedTask;
            }
        };
        var credentialStore = new FakeCredentialStore
        {
            StoredSession = new SessionCredentials(expiredCookie, SessionSource.ManualCookie, DateTimeOffset.Now.AddHours(-2), true)
        };
        var service = new SessionService(apiClient, credentialStore, new ActivityLogService(), runtimeState);

        var restored = await service.RestoreAsync();

        Assert.NotNull(restored);
        Assert.Equal(refreshedCookie, restored.Cookie);
    }

    [Fact]
    public async Task RestoreAsync_ClearsStoredSession_WhenStoredCookieIsInvalid()
    {
        var apiClient = new FakeTraceIntApiClient
        {
            OnValidateCookieAsync = (_, _) => Task.FromException(new InvalidOperationException("Cookie 已失效"))
        };
        var credentialStore = new FakeCredentialStore
        {
            StoredSession = new SessionCredentials("expired-cookie", SessionSource.ManualCookie, DateTimeOffset.Now.AddDays(-1), true)
        };
        var runtimeState = new AppRuntimeState();
        var service = new SessionService(apiClient, credentialStore, new ActivityLogService(), runtimeState);

        var restored = await service.RestoreAsync();

        Assert.Null(restored);
        Assert.Null(runtimeState.Session);
        Assert.Equal(1, credentialStore.ClearCalls);
        Assert.Null(credentialStore.StoredSession);
    }

    [Fact]
    public async Task RestoreAsync_DoesNotClearStoredSession_WhenValidationFailsTransiently()
    {
        var apiClient = new FakeTraceIntApiClient
        {
            OnValidateCookieAsync = (_, _) => Task.FromException(new HttpRequestException("temporary", null, HttpStatusCode.ServiceUnavailable))
        };
        var storedSession = new SessionCredentials("cookie", SessionSource.ManualCookie, DateTimeOffset.Now.AddDays(-1), true);
        var credentialStore = new FakeCredentialStore
        {
            StoredSession = storedSession
        };
        var runtimeState = new AppRuntimeState();
        var service = new SessionService(apiClient, credentialStore, new ActivityLogService(), runtimeState);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.RestoreAsync());

        Assert.Equal(0, credentialStore.ClearCalls);
        Assert.Equal(storedSession, credentialStore.StoredSession);
        Assert.Null(runtimeState.Session);
    }

    [Fact]
    public async Task RestoreAsync_ReturnsCookieUpdatedDuringValidation()
    {
        var runtimeState = new AppRuntimeState();
        var storedSession = new SessionCredentials("Authorization=old-token; SERVERID=old-server", SessionSource.ManualCookie, DateTimeOffset.Now.AddDays(-1), true);
        var apiClient = new FakeTraceIntApiClient
        {
            OnValidateCookieAsync = (_, _) =>
            {
                runtimeState.Session = runtimeState.Session! with { Cookie = "Authorization=new-token; SERVERID=new-server" };
                return Task.CompletedTask;
            }
        };
        var credentialStore = new FakeCredentialStore
        {
            StoredSession = storedSession
        };
        var service = new SessionService(apiClient, credentialStore, new ActivityLogService(), runtimeState);

        var restored = await service.RestoreAsync();

        Assert.NotNull(restored);
        Assert.Equal(SessionSource.Restored, restored.Source);
        Assert.Equal("Authorization=new-token; SERVERID=new-server", restored.Cookie);
        Assert.Equal(restored, runtimeState.Session);
    }

    [Fact]
    public async Task RestoreAsync_ClearsStoredSession_WhenStoredPayloadIsBroken()
    {
        var credentialStore = new FakeCredentialStore
        {
            LoadException = new JsonException("bad json")
        };
        var runtimeState = new AppRuntimeState();
        var service = new SessionService(new FakeTraceIntApiClient(), credentialStore, new ActivityLogService(), runtimeState);

        var restored = await service.RestoreAsync();

        Assert.Null(restored);
        Assert.Equal(1, credentialStore.ClearCalls);
    }

    private static string BuildAuthorizationCookie(DateTimeOffset expiresAt)
    {
        var header = Base64Url("""{"typ":"JWT","alg":"RS256"}""");
        var payload = Base64Url($$"""{"userId":37580434,"schId":20175,"expireAt":{{expiresAt.ToUnixTimeSeconds()}},"tag":"cookie-test"}""");
        return $"Authorization={header}.{payload}.signature; SERVERID=test-server|1777956374|1777956374";
    }

    private static string Base64Url(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
