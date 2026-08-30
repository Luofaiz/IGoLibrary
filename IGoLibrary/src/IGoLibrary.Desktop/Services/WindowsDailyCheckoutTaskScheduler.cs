using System.Diagnostics;
using System.Security.Principal;
using System.Xml.Linq;

namespace IGoLibrary.Desktop.Services;

public sealed class WindowsDailyCheckoutTaskScheduler : IDailyCheckoutTaskScheduler
{
    internal const string TaskName = "IGoLibrary Daily Checkout";
    internal static readonly TimeSpan DefaultCheckoutTime = new(21, 30, 0);

    public async Task ConfigureAsync(bool enabled, TimeSpan checkoutTime, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (enabled)
            {
                throw new PlatformNotSupportedException("每日自动退座目前仅支持 Windows。");
            }

            return;
        }

        if (!enabled)
        {
            await DeleteTaskIfPresentAsync(cancellationToken);
            return;
        }

        if (checkoutTime < TimeSpan.Zero || checkoutTime >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(checkoutTime), "退座时间必须在 00:00 到 23:59 之间。");
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            throw new InvalidOperationException("无法确定 IGoLibrary 程序路径，未能创建每日退座任务。");
        }

        var userSid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
        {
            throw new InvalidOperationException("无法识别当前 Windows 用户，未能创建每日退座任务。");
        }

        var taskXml = BuildTaskXml(executablePath, userSid, DateTime.Now, checkoutTime);
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"IGoLibrary-DailyCheckout-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, taskXml, System.Text.Encoding.Unicode, cancellationToken);
            var result = await RunSchtasksAsync(
                ["/Create", "/TN", TaskName, "/XML", temporaryPath, "/F"],
                cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(BuildSchtasksError("创建", result));
            }
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    public async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var result = await RunSchtasksAsync(["/Query", "/TN", TaskName], cancellationToken);
        return result.ExitCode == 0;
    }

    internal static string BuildTaskXml(string executablePath, string userSid, DateTime now, TimeSpan? checkoutTime = null)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var startDate = now.Date.Add(checkoutTime ?? DefaultCheckoutTime);
        if (startDate <= now)
        {
            startDate = startDate.AddDays(1);
        }

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(ns + "Task",
                new XAttribute("version", "1.4"),
                new XElement(ns + "RegistrationInfo",
                    new XElement(ns + "Description", $"IGoLibrary 每天 {startDate:HH\\:mm} 自动恢复会话并退出学习中的座位。")),
                new XElement(ns + "Triggers",
                    new XElement(ns + "CalendarTrigger",
                        new XElement(ns + "StartBoundary", startDate.ToString("yyyy-MM-dd'T'HH:mm:ss")),
                        new XElement(ns + "Enabled", "true"),
                        new XElement(ns + "ScheduleByDay",
                            new XElement(ns + "DaysInterval", "1")))),
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(ns + "UserId", userSid),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", "LeastPrivilege"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(ns + "AllowHardTerminate", "true"),
                    new XElement(ns + "StartWhenAvailable", "true"),
                    new XElement(ns + "RunOnlyIfNetworkAvailable", "true"),
                    new XElement(ns + "AllowStartOnDemand", "true"),
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "Hidden", "false"),
                    new XElement(ns + "RunOnlyIfIdle", "false"),
                    new XElement(ns + "WakeToRun", "true"),
                    new XElement(ns + "ExecutionTimeLimit", "PT5M"),
                    new XElement(ns + "Priority", "7")),
                new XElement(ns + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", executablePath),
                        new XElement(ns + "Arguments", "--scheduled-checkout"),
                        new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(executablePath))))));

        return document.ToString();
    }

    private static async Task DeleteTaskIfPresentAsync(CancellationToken cancellationToken)
    {
        var queryResult = await RunSchtasksAsync(["/Query", "/TN", TaskName], cancellationToken);
        if (queryResult.ExitCode != 0)
        {
            return;
        }

        var deleteResult = await RunSchtasksAsync(["/Delete", "/TN", TaskName, "/F"], cancellationToken);
        if (deleteResult.ExitCode != 0)
        {
            throw new InvalidOperationException(BuildSchtasksError("删除", deleteResult));
        }
    }

    private static async Task<SchtasksResult> RunSchtasksAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 Windows 任务计划程序命令。");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new SchtasksResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static string BuildSchtasksError(string operation, SchtasksResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        return $"{operation}每日自动退座任务失败（退出码 {result.ExitCode}）：{detail.Trim()}";
    }

    private sealed record SchtasksResult(int ExitCode, string Output, string Error);
}
