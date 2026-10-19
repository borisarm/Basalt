using System.Text.Json;
using System.Text.Json.Serialization;
using Basalt.Domain.Accounts;
using Basalt.Domain.Vaults;

namespace Basalt.Adapters.Platform.Json;

internal sealed class VaultIdConverter : JsonConverter<VaultId>
{
    public override VaultId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var parsed = VaultId.Parse(reader.GetString() ?? string.Empty);
        return parsed.IsSuccess ? parsed.Value : throw new JsonException(parsed.Error.Message);
    }

    public override void Write(Utf8JsonWriter writer, VaultId value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

internal sealed class GitHubAccountIdConverter : JsonConverter<GitHubAccountId>
{
    public override GitHubAccountId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetGuid());

    public override void Write(Utf8JsonWriter writer, GitHubAccountId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}

internal sealed class GitHubHostConverter : JsonConverter<GitHubHost>
{
    public override GitHubHost Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString() ?? string.Empty;
        var parsed = GitHubHost.Create(text);
        if (parsed.IsSuccess)
        {
            return parsed.Value;
        }

        // Hosts created for tests (http loopback fake servers) are persisted as their base URL.
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? GitHubHost.ForTesting(uri) : throw new JsonException(parsed.Error.Message);
    }

    public override void Write(Utf8JsonWriter writer, GitHubHost value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
