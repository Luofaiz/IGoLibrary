namespace IGoLibrary.Ex.Desktop.Services;

public interface IDailyCheckoutTaskScheduler
{
    Task ConfigureAsync(bool enabled, TimeSpan checkoutTime, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(CancellationToken cancellationToken = default);
}
