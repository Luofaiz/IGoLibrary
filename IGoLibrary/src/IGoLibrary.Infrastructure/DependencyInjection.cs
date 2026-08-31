using System.Net;
using IGoLibrary.Application.Abstractions;
using IGoLibrary.Infrastructure.Api;
using IGoLibrary.Infrastructure.Logging;
using IGoLibrary.Infrastructure.Notifications;
using IGoLibrary.Infrastructure.Persistence;
using IGoLibrary.Infrastructure.Protocol;
using IGoLibrary.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IGoLibrary.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<IAppDataInitializer, SqliteAppDataInitializer>();
        services.TryAddSingleton<IAppLogWriter, AppLogFileWriter>();
        services.AddSingleton<AppTraceListener>();
        services.AddSingleton<TraceListenerRegistrar>();
        services.AddSingleton<ISettingsRepository, SqliteSettingsRepository>();
        services.AddSingleton<IFavoritesRepository, SqliteFavoritesRepository>();
        services.AddSingleton<ITaskLaunchHistoryService, SqliteTaskLaunchHistoryService>();
        services.AddSingleton<IProtocolTemplateStore, DefaultProtocolTemplateStore>();
        services.AddSingleton<ICredentialStore>(_ => PlatformCredentialStore.CreateDefault());
        services.AddSingleton<IPrereserveQueueClient, PrereserveQueueClient>();
        services.AddSingleton<ISmtpTransportClientFactory, MailKitSmtpTransportClientFactory>();
        services.AddSingleton<IEmailAlertSender, SmtpEmailAlertSender>();

        services.AddSingleton<TraceIntRequestPolicy>();
        services.AddHttpClient<TraceIntGraphQlTransport>(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = false
            });
        services.AddHttpClient<ITraceIntApiClient, TraceIntApiClient>(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = false
            });

        return services;
    }
}
