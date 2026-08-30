using IGoLibrary.Domain.Models;

namespace IGoLibrary.Application.Abstractions;

public interface IEmailAlertSender
{
    Task SendAsync(
        CookieExpiryEmailAlertSettings settings,
        string subject,
        string body,
        CancellationToken cancellationToken = default);
}
