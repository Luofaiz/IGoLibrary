using IGoLibrary.Domain.Enums;
using IGoLibrary.Domain.Models;

namespace IGoLibrary.Domain.Helpers;

public static class GrabStrategyFactory
{
    public static GrabSeatPollingStrategy FromMode(GrabMode mode)
    {
        return mode switch
        {
            GrabMode.Aggressive => new GrabSeatPollingStrategy(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                50,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10)),
            GrabMode.Randomized => new GrabSeatPollingStrategy(
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(8),
                50,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10)),
            _ => new GrabSeatPollingStrategy(
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(3),
                50,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10))
        };
    }
}
