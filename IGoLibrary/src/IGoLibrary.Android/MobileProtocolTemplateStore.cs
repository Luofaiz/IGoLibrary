using IGoLibrary.Application.Abstractions;
using IGoLibrary.Domain.Models;
using IGoLibrary.Infrastructure.Protocol;

namespace IGoLibrary.Android;

internal sealed class MobileProtocolTemplateStore : IProtocolTemplateStore
{
    public Task<ProtocolTemplateSet> GetEffectiveTemplatesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(DefaultTemplates.Instance);

    public Task SaveOverridesAsync(ProtocolTemplateOverrides overrides, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task ResetOverridesAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
