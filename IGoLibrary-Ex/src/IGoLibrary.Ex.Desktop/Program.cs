using Avalonia;
using System.Diagnostics;
using IGoLibrary.Ex.Application.Abstractions;
using IGoLibrary.Ex.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IGoLibrary.Ex.Desktop;

internal static class Program
{
    private const string ScheduledCheckoutArgument = "--scheduled-checkout";
    private static bool _globalExceptionLoggingRegistered;
    private static int _skipNextUnhandledExceptionLog;
    public static IHost? Host { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        using var sharedLogWriter = new AppLogFileWriter();
        RegisterGlobalExceptionLogging(sharedLogWriter);

        try
        {
            Host = HostBuilderFactory.Create(args, sharedLogWriter).Build();
            Host.Start();
            Host.Services.GetRequiredService<TraceListenerRegistrar>().Attach();
            if (args.Contains(ScheduledCheckoutArgument, StringComparer.OrdinalIgnoreCase))
            {
                RunScheduledCheckoutAsync(Host.Services, sharedLogWriter).GetAwaiter().GetResult();
                return;
            }

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _skipNextUnhandledExceptionLog, 1);
            sharedLogWriter.Write(LogLevel.Critical, "Bootstrap", "应用启动失败。", ex);
            sharedLogWriter.Flush();
            throw;
        }
        finally
        {
            try
            {
                Trace.Flush();
            }
            catch
            {
            }

            if (Host is not null)
            {
                try
                {
                    Host.StopAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    sharedLogWriter.Write(LogLevel.Error, "Bootstrap", "停止主机时发生异常。", ex);
                    sharedLogWriter.Flush();
                }
                finally
                {
                    try
                    {
                        Host.Dispose();
                    }
                    catch (Exception ex)
                    {
                        sharedLogWriter.Write(LogLevel.Error, "Bootstrap", "释放主机时发生异常。", ex);
                        sharedLogWriter.Flush();
                    }
                    finally
                    {
                        Host = null;
                    }
                }
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static async Task RunScheduledCheckoutAsync(IServiceProvider services, IAppLogWriter logWriter)
    {
        await services.GetRequiredService<IAppDataInitializer>().InitializeAsync();
        var result = await services.GetRequiredService<IDailyCheckoutService>().RunAsync();
        if (result.Succeeded)
        {
            logWriter.Write(LogLevel.Information, "DailyCheckout", result.Message);
            Environment.ExitCode = 0;
            return;
        }

        logWriter.Write(LogLevel.Error, "DailyCheckout", result.Message);
        try
        {
            var settings = await services.GetRequiredService<ISettingsService>().LoadAsync();
            var emailSettings = settings.CookieExpiryAlerts?.Email;
            if (emailSettings is { Enabled: true })
            {
                await services.GetRequiredService<IEmailAlertSender>().SendAsync(
                    emailSettings,
                    "IGoLibrary 每日自动退座失败",
                    $"执行时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}{Environment.NewLine}" +
                    $"失败原因：{result.Message}{Environment.NewLine}{Environment.NewLine}" +
                    "请打开 IGoLibrary 检查登录状态和任务日志。");
            }
        }
        catch (Exception ex)
        {
            logWriter.Write(LogLevel.Warning, "DailyCheckout", $"发送自动退座失败邮件时出错：{ex.Message}", ex);
        }

        Environment.ExitCode = 2;
    }

    private static void RegisterGlobalExceptionLogging(IAppLogWriter logWriter)
    {
        if (_globalExceptionLoggingRegistered)
        {
            return;
        }

        _globalExceptionLoggingRegistered = true;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (Interlocked.Exchange(ref _skipNextUnhandledExceptionLog, 0) == 1)
            {
                return;
            }

            if (args.ExceptionObject is Exception exception)
            {
                logWriter.Write(LogLevel.Critical, "Global", "捕获到未处理的应用程序异常。", exception);
                logWriter.Flush();
                return;
            }

            logWriter.Write(
                LogLevel.Critical,
                "Global",
                $"捕获到未处理的应用程序异常：{args.ExceptionObject}");
            logWriter.Flush();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            logWriter.Write(LogLevel.Error, "Global", "捕获到未观察的后台任务异常。", args.Exception);
            logWriter.Flush();
        };
    }
}
