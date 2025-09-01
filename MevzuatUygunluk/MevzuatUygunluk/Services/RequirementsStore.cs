using System.Text.Json;
using MevzuatUygunluk.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace MevzuatUygunluk.Services;

public interface IRequirementsStore
{
    Task SaveAsync(GeneratedRequirements data, CancellationToken ct = default);
    Task<GeneratedRequirements?> LoadAsync(CancellationToken ct = default);
    string StorePath { get; }
}

public class RequirementsStore : IRequirementsStore
{
    private readonly string _path;

    public RequirementsStore(IConfiguration cfg, IWebHostEnvironment env)
    {
        var rel = cfg["Regulations:RequirementsStorePath"] ?? "Data/requirements.json";
        _path = Path.Combine(env.ContentRootPath, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public string StorePath => _path;

    public async Task SaveAsync(GeneratedRequirements data, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_path, json, ct);
    }

    public async Task<GeneratedRequirements?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return null;
        var json = await File.ReadAllTextAsync(_path, ct);
        return JsonSerializer.Deserialize<GeneratedRequirements>(json);
    }
}
