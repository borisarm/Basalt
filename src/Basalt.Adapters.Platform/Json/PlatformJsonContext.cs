using System.Text.Json.Serialization;
using Basalt.Application.Accounts;
using Basalt.Application.Settings;
using Basalt.Application.Vaults;
using Basalt.Application.Workspace;
using Basalt.Domain.Vaults;

namespace Basalt.Adapters.Platform.Json;

/// <summary>JSON shape of <c>.basalt/vault.json</c>.</summary>
internal sealed record VaultConfigDto(VaultId VaultId, VaultSettings Settings);

/// <summary>Source-generated serialisation (trimming/AOT-safe, spec 01 §6) for the files this adapter owns.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    Converters = [typeof(VaultIdConverter), typeof(GitHubAccountIdConverter), typeof(GitHubHostConverter)])]
[JsonSerializable(typeof(VaultRegistryDocument))]
[JsonSerializable(typeof(AccountsDocument))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(VaultConfigDto))]
[JsonSerializable(typeof(WorkspaceState))]
[JsonSerializable(typeof(RecentFilesDocument))]
[JsonSerializable(typeof(DeviceDocument))]
internal sealed partial class PlatformJsonContext : JsonSerializerContext;
