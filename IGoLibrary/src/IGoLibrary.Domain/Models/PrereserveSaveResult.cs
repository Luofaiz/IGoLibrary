namespace IGoLibrary.Domain.Models;

public sealed record PrereserveSaveResult(
    bool Submitted,
    string UpdatedCookie,
    string? Message = null);
