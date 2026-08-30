using IGoLibrary.Domain.Enums;
using IGoLibrary.Domain.Models;

namespace IGoLibrary.Application.Abstractions;

public interface IActivityLogService
{
    event EventHandler<AppLogEntry>? EntryWritten;

    IReadOnlyList<AppLogEntry> Entries { get; }

    void Write(LogEntryKind kind, string category, string message);
}
