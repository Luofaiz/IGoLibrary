using IGoLibrary.Domain.Enums;

namespace IGoLibrary.Domain.Models;

public sealed record AppLogEntry(
    DateTimeOffset Timestamp,
    LogEntryKind Kind,
    string Category,
    string Message);
