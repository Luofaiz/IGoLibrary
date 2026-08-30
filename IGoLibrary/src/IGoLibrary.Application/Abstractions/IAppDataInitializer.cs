namespace IGoLibrary.Application.Abstractions;

public interface IAppDataInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
