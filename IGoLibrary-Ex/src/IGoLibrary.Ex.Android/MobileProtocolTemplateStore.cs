using IGoLibrary.Ex.Application.Abstractions;
using IGoLibrary.Ex.Domain.Models;
using IGoLibrary.Ex.Infrastructure.Protocol;

namespace IGoLibrary.Ex.Android;

internal sealed class MobileProtocolTemplateStore : IProtocolTemplateStore
{
    public Task<ProtocolTemplateSet> GetEffectiveTemplatesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(DefaultTemplates.Instance);

    public Task SaveOverridesAsync(ProtocolTemplateOverrides overrides, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task ResetOverridesAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
