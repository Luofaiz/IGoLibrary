using IGoLibrary.Domain.Models;

namespace IGoLibrary.Application.Abstractions;

public interface IPrereserveQueueClient
{
    Task RunAsync(
        string cookie,
        Func<PrereserveQueueMessage, CancellationToken, Task> onMessageAsync,
        CancellationToken cancellationToken = default);

    async Task RunAsync(
        string cookie,
        Func<PrereserveQueueMessage, CancellationToken, Task> onMessageAsync,
        DateTimeOffset? scheduledStart,
        CancellationToken cancellationToken = default)
        => await RunAsync(cookie, onMessageAsync, cancellationToken);
}
