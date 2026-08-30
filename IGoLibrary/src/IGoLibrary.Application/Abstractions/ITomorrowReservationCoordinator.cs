using IGoLibrary.Domain.Models;

namespace IGoLibrary.Application.Abstractions;

public interface ITomorrowReservationCoordinator
{
    event EventHandler<CoordinatorStatus>? StatusChanged;

    Task StartAsync(TomorrowReservationPlan plan, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    CoordinatorStatus GetStatus();
}
