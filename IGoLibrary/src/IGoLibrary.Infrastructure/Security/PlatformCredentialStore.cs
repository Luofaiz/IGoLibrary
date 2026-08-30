using System.Runtime.InteropServices;
using IGoLibrary.Application.Abstractions;

namespace IGoLibrary.Infrastructure.Security;

public static class PlatformCredentialStore
{
    public static ICredentialStore CreateDefault()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowsCredentialStore();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new MacKeychainCredentialStore();
        }

        return new InMemoryCredentialStore();
    }
}
