using System.Xml.Linq;
using IGoLibrary.Desktop.Services;

namespace IGoLibrary.Tests;

public sealed class WindowsDailyCheckoutTaskSchedulerTests
{
    [Fact]
    public void BuildTaskXml_CreatesDailyWakeTaskAtTwentyOneThirty()
    {
        const string executablePath = @"C:\Program Files\IGoLibrary & Tools\IGoLibrary.exe";
        const string sid = "S-1-5-21-1-2-3-1001";

        var xml = WindowsDailyCheckoutTaskScheduler.BuildTaskXml(
            executablePath,
            sid,
            new DateTime(2026, 8, 29, 22, 0, 0));
        var document = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal("2026-08-30T21:30:00", document.Descendants(ns + "StartBoundary").Single().Value);
        Assert.Equal("1", document.Descendants(ns + "DaysInterval").Single().Value);
        Assert.Equal("true", document.Descendants(ns + "WakeToRun").Single().Value);
        Assert.Equal("true", document.Descendants(ns + "StartWhenAvailable").Single().Value);
        Assert.Equal(sid, document.Descendants(ns + "UserId").Single().Value);
        Assert.Equal(executablePath, document.Descendants(ns + "Command").Single().Value);
        Assert.Equal("--scheduled-checkout", document.Descendants(ns + "Arguments").Single().Value);
    }

    [Fact]
    public void BuildTaskXml_UsesConfiguredCheckoutTime()
    {
        var xml = WindowsDailyCheckoutTaskScheduler.BuildTaskXml(
            @"C:\IGoLibrary\IGoLibrary.exe",
            "S-1-5-21-1-2-3-1001",
            new DateTime(2026, 8, 29, 7, 0, 0),
            new TimeSpan(8, 15, 0));
        var document = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal("2026-08-29T08:15:00", document.Descendants(ns + "StartBoundary").Single().Value);
        Assert.Contains("每天 08:15", document.Descendants(ns + "Description").Single().Value, StringComparison.Ordinal);
    }
}
