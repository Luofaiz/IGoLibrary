using IGoLibrary.Domain.Models;

namespace IGoLibrary.Application.Abstractions;

public interface IProtocolTemplateStore
{
    Task<ProtocolTemplateSet> GetEffectiveTemplatesAsync(CancellationToken cancellationToken = default);

    Task SaveOverridesAsync(ProtocolTemplateOverrides overrides, CancellationToken cancellationToken = default);

    Task ResetOverridesAsync(CancellationToken cancellationToken = default);
}
