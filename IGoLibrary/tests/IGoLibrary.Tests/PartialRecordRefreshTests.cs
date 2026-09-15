using IGoLibrary.Infrastructure.Api;
using IGoLibrary.Domain.Models;
using IGoLibrary.Domain.Enums;

namespace IGoLibrary.Tests;

public sealed class PartialRecordRefreshTests
{
    private static TraceIntApiClient Create(params string[] responses) => new(
        new HttpClient(new SequenceHttpMessageHandler(responses.Select<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>(
            json => (_, _) => SequenceHttpMessageHandler.JsonResponseAsync(json)).ToArray())),
        new FakeProtocolTemplateStore(new ProtocolTemplateSet("{}", "{}", "{}", "{}", "{}", "{}", "{}")),
        new FakeSettingsService(AppSettings.Default with { RetryCount = 0 }));

    [Fact]
    public async Task PartialFailure_DoesNotHideSuccessfulOtherPartition()
    {
        var api = Create("{}", """{"data":{"userAuth":{"prereserve":{"prereserve":null}}}}""");
        var result = await api.RefreshReservationRecordsAsync("cookie");
        Assert.NotNull(result.TodayError);
        Assert.Null(result.TomorrowError);
        api = Create("""{"data":{"userAuth":{"reserve":{"reserve":null}}}}""", "{}");
        result = await api.RefreshReservationRecordsAsync("cookie");
        Assert.Null(result.TodayError);
        Assert.NotNull(result.TomorrowError);
    }
}

public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public void PartialRefresh_KeepsFailedPartitionButClearsConfirmedEmptyPartition()
    {
        var vm = CreateViewModel();
        var method = vm.GetType().GetMethod("ApplyReservationRefresh", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var today = new ReservationRecord(ReservationRecordKind.Today, "token", 1, "One", "a", "A", DateTimeOffset.Now.AddHours(1), null);
        var tomorrow = new ReservationRecord(ReservationRecordKind.Tomorrow, "", 1, "One", "b", "B", null, DateOnly.FromDateTime(DateTime.Today.AddDays(1)));
        method.Invoke(vm, [new ReservationRecordsRefresh([today, tomorrow])]);
        method.Invoke(vm, [new ReservationRecordsRefresh([], new InvalidOperationException("offline"), null)]);
        Assert.Single(vm.HomeReservationRecords);
        Assert.Contains("保留旧记录", vm.ReservationRefreshStatusText);
        method.Invoke(vm, [new ReservationRecordsRefresh([])]);
        Assert.Empty(vm.HomeReservationRecords);
    }
}
