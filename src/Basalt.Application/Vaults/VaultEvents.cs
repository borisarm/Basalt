using Basalt.Application.Ports.Events;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Vaults;

public sealed record VaultOpened(VaultId Id) : IApplicationEvent
{
    VaultId? IApplicationEvent.VaultId => Id;
}

public sealed record VaultClosed(VaultId Id) : IApplicationEvent
{
    VaultId? IApplicationEvent.VaultId => Id;
}

public sealed record VaultRemovedFromList(VaultId Id) : IApplicationEvent
{
    VaultId? IApplicationEvent.VaultId => Id;
}

public sealed record VaultSettingsChanged(VaultId Id, VaultSettings Settings) : IApplicationEvent
{
    VaultId? IApplicationEvent.VaultId => Id;
}
