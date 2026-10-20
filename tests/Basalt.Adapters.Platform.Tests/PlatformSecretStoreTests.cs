using Basalt.Application.Ports.Security;
using Basalt.Testing.Contracts;

namespace Basalt.Adapters.Platform.Tests;

public sealed class PlatformSecretStoreTests : SecretStoreContract
{
    protected override Task<ISecretStore> CreateSutAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The Windows Credential Manager is verified on Windows; Apple Keychain runs on macOS/iOS.");
        }

        return Task.FromResult<ISecretStore>(new PlatformSecretStore());
    }
}
