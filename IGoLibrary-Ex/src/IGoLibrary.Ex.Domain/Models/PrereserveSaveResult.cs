namespace IGoLibrary.Ex.Domain.Models;

public sealed record PrereserveSaveResult(
    bool Submitted,
    string UpdatedCookie,
    string? Message = null);
