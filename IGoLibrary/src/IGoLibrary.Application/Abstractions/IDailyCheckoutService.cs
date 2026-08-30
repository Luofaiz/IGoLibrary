namespace IGoLibrary.Application.Abstractions;

public interface IDailyCheckoutService
{
    Task<DailyCheckoutRunResult> RunAsync(CancellationToken cancellationToken = default);
}

public sealed record DailyCheckoutRunResult(
    bool Succeeded,
    bool SeatReleased,
    string Message)
{
    public static DailyCheckoutRunResult NoReservation() =>
        new(true, false, "当前没有今日座位，无需退座。");

    public static DailyCheckoutRunResult Released(string seatName) =>
        new(true, true, $"{seatName} 已成功退座。");

    public static DailyCheckoutRunResult Failed(string message) =>
        new(false, false, message);
}
