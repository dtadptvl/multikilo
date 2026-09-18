using System.Text.Json;
using MultiKilo.Models;

namespace MultiKilo.Services;

public sealed class ProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "projects.json");

    public IReadOnlyList<ProjectDefinition> Load()
    {
        if (!File.Exists(FilePath))
        {
            return Array.Empty<ProjectDefinition>();
        }

        using var stream = File.OpenRead(FilePath);
        return JsonSerializer.Deserialize<List<ProjectDefinition>>(stream, JsonOptions)
               ?? Array.Empty<ProjectDefinition>();
    }

    public void Save(IEnumerable<ProjectDefinition> projects)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var tempPath = FilePath + ".tmp";

        using (var stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, projects, JsonOptions);
        }

        File.Move(tempPath, FilePath, overwrite: true);
    }
}
