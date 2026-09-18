using System.Text.Json.Serialization;

namespace MultiKilo.Models;

public sealed class ProjectDefinition
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("folder")]
    public string Folder { get; init; } = string.Empty;
}
