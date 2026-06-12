using IGoLibrary.Ex.Domain.Enums;

namespace IGoLibrary.Ex.Domain.Models;

public sealed record OccupySeatPlan(
    TimeSpan ReReserveLeadTime,
    RefreshMode RefreshMode,
    OccupyReReserveTriggerMode TriggerMode = OccupyReReserveTriggerMode.BeforeExpiration,
    TimeOnly? ScheduledReReserveTime = null);
