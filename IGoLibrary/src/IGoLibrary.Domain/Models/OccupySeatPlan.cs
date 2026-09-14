using IGoLibrary.Domain.Enums;

namespace IGoLibrary.Domain.Models;

public sealed record OccupySeatPlan(
    TimeSpan ReReserveLeadTime,
    RefreshMode RefreshMode);
