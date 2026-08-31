using System.Net;
using IGoLibrary.Application.Abstractions;
using IGoLibrary.Domain.Models;

namespace IGoLibrary.Infrastructure.Api;

/// Centralizes timeout, cancellation, and transient HTTP retry behavior for TraceInt calls.
public sealed class TraceIntRequestPolicy(ISettingsService settingsService)
{
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        AppSettings settings;
        try
        {
            settings = await settingsService.LoadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            settings = AppSettings.Default;
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(settings.ApiTimeoutSeconds, 1, 60));
        var retries = Math.Clamp(settings.RetryCount, 0, 10);
        Exception? last = null;

        for (var attempt = 0; attempt <= retries; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                return await operation(timeoutCts.Token);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                last = new TimeoutException($"请求超时（>{timeout.TotalSeconds:0} 秒）。", ex);
            }
            catch (HttpRequestException ex) when (IsTransient(ex.StatusCode))
            {
                last = ex;
            }

            if (attempt < retries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken);
            }
        }

        throw last ?? new InvalidOperationException("请求失败。");
    }

    internal static bool IsTransient(HttpStatusCode? statusCode) =>
        statusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout || (int?)statusCode >= 500;
}
