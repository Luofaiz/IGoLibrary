using IGoLibrary.Application;
using IGoLibrary.Application.Abstractions;
using IGoLibrary.Desktop.Services;
using IGoLibrary.Desktop.ViewModels;
using IGoLibrary.Infrastructure;
using IGoLibrary.Infrastructure.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IGoLibrary.Desktop;

internal static class HostBuilderFactory
{
    public static IHostBuilder Create(string[] args, IAppLogWriter? sharedLogWriter = null)
    {
        return Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration(config =>
            {
                config.SetBasePath(AppContext.BaseDirectory);
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Information);
                logging.AddDebug();
                logging.Services.AddSingleton<ILoggerProvider, AppFileLoggerProvider>();
            })
            .ConfigureServices(services =>
            {
                if (sharedLogWriter is not null)
                {
                    services.AddSingleton(sharedLogWriter);
                }

                services.AddApplication();
                services.AddInfrastructure();
                services.AddSingleton<IAppThemeService, AppThemeService>();
                services.AddSingleton<AppWindowService>();
                services.AddSingleton<IErrorDialogService, ErrorDialogService>();
                services.AddSingleton<IConfirmationDialogService, ConfirmationDialogService>();
                services.AddSingleton<IAppUpdateService, GitHubReleaseUpdateService>();
                services.AddSingleton<IDailyCheckoutTaskScheduler, WindowsDailyCheckoutTaskScheduler>();
                services.AddSingleton<ToastNotificationService>();
                services.AddSingleton<INotificationService>(serviceProvider => serviceProvider.GetRequiredService<ToastNotificationService>());
                services.AddSingleton<AlertSoundService>();
                services.AddSingleton<ITaskAlertService, TaskAlertService>();
                services.AddSingleton<MainWindow>();
                services.AddSingleton<MainWindowViewModel>();
            });
    }
}
