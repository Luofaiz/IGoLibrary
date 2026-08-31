using System.Net;
using IGoLibrary.Infrastructure.Api;
using IGoLibrary.Domain.Models;

namespace IGoLibrary.Tests;

public sealed class TraceIntRequestPolicyTests
{
    [Fact]
    public async Task ExecuteAsync_RetriesTransientFailures()
    {
        var attempts = 0;
        var settings = new FakeSettingsService(AppSettings.Default with { ApiTimeoutSeconds = 5, RetryCount = 2 });
        var policy = new TraceIntRequestPolicy(settings);

        var result = await policy.ExecuteAsync<int>(_ =>
        {
            attempts++;
            if (attempts < 3)
            {
                throw new HttpRequestException("busy", null, HttpStatusCode.ServiceUnavailable);
            }

            return Task.FromResult(42);
        });

        Assert.Equal(42, result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCallerCancellationWithoutRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var settings = new FakeSettingsService(AppSettings.Default with { RetryCount = 3 });
        var policy = new TraceIntRequestPolicy(settings);
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await policy.ExecuteAsync(async token =>
            {
                attempts++;
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 1;
            }, cancellation.Token));

        Assert.Equal(1, attempts);
    }
}
