using IGoLibrary.Domain.Models;

namespace IGoLibrary.Application.Abstractions;

public interface IPrereserveQueueClient
{
    Task RunAsync(
        string cookie,
        Func<PrereserveQueueMessage, CancellationToken, Task> onMessageAsync,
        CancellationToken cancellationToken = default);
}
