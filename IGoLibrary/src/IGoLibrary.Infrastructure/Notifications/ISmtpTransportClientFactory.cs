namespace IGoLibrary.Infrastructure.Notifications;

internal interface ISmtpTransportClientFactory
{
    ISmtpTransportClient Create();
}
