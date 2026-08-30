using IGoLibrary.Domain.Enums;

namespace IGoLibrary.Domain.Models;

public sealed record SessionCredentials(
    string Cookie,
    SessionSource Source,
    DateTimeOffset SavedAt,
    bool CanAutoRestore);
