using IGoLibrary.Ex.Domain.Models;

namespace IGoLibrary.Ex.Application.Abstractions;

public interface IPrereserveQueueClient
{
    Task RunAsync(
        string cookie,
        Func<PrereserveQueueMessage, CancellationToken, Task> onMessageAsync,
        CancellationToken cancellationToken = default);
}
