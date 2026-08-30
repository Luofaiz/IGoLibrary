using System.Text.Json;
using Android.Content;
using IGoLibrary.Application.Abstractions;
using IGoLibrary.Domain.Models;

namespace IGoLibrary.Android;

internal sealed class MobileCredentialStore(Func<ISharedPreferences> getPreferences) : ICredentialStore
{
    private const string SessionKey = "session";
    private const string CookieKey = "cookie";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public Task SaveSessionAsync(SessionCredentials credentials, CancellationToken cancellationToken = default)
    {
        var preferences = getPreferences();
        var json = JsonSerializer.Serialize(credentials, JsonOptions);
        var editor = preferences.Edit();
        editor?.PutString(SessionKey, json);
        editor?.PutString(CookieKey, credentials.Cookie);
        editor?.Apply();
        return Task.CompletedTask;
    }

    public Task<SessionCredentials?> LoadSessionAsync(CancellationToken cancellationToken = default)
    {
        var preferences = getPreferences();
        var json = preferences.GetString(SessionKey, string.Empty);
        if (!string.IsNullOrWhiteSpace(json))
        {
            return Task.FromResult(JsonSerializer.Deserialize<SessionCredentials>(json, JsonOptions));
        }

        var cookie = preferences.GetString(CookieKey, string.Empty);
        return string.IsNullOrWhiteSpace(cookie)
            ? Task.FromResult<SessionCredentials?>(null)
            : Task.FromResult<SessionCredentials?>(new SessionCredentials(
                cookie,
                IGoLibrary.Domain.Enums.SessionSource.Restored,
                DateTimeOffset.Now,
                true));
    }

    public Task ClearSessionAsync(CancellationToken cancellationToken = default)
    {
        var editor = getPreferences().Edit();
        editor?.Remove(SessionKey);
        editor?.Remove(CookieKey);
        editor?.Apply();
        return Task.CompletedTask;
    }
}
