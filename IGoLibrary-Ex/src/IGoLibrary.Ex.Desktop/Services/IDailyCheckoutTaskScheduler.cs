namespace IGoLibrary.Ex.Desktop.Services;

public interface IDailyCheckoutTaskScheduler
{
    Task ConfigureAsync(bool enabled, CancellationToken cancellationToken = default);
}
