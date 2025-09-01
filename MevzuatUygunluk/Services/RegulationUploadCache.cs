using System.Security.Cryptography;
using System.Text.Json;

namespace MevzuatUygunluk.Services;

public interface IRegulationUploadCache
{
    Task<(string fileUri, string mimeType)> GetOrUploadAsync(
        string localPath, IGeminiService gemini, CancellationToken ct = default);
}

public class RegulationUploadCache : IRegulationUploadCache
{
    private readonly string _storePath;
    private readonly Dictionary<string, CacheItem> _map = new();

    private class CacheItem
    {
        public string Sha256 { get; set; } = "";
        public string FileUri { get; set; } = "";
        public string MimeType { get; set; } = "application/octet-stream";
    }

    public RegulationUploadCache(IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "Data");
        Directory.CreateDirectory(dir);
        _storePath = Path.Combine(dir, "upload_cache.json");
        if (File.Exists(_storePath))
        {
            var json = File.ReadAllText(_storePath);
            var temp = JsonSerializer.Deserialize<Dictionary<string, CacheItem>>(json);
            if (temp != null) _map = temp;
        }
    }

    public async Task<(string fileUri, string mimeType)> GetOrUploadAsync(
        string localPath, IGeminiService gemini, CancellationToken ct = default)
    {
        if (!File.Exists(localPath))
            throw new FileNotFoundException(localPath);

        var sha = await HashAsync(localPath, ct);
        if (_map.TryGetValue(localPath, out var item) && item.Sha256 == sha)
            return (item.FileUri, item.MimeType);

        var uploaded = await gemini.UploadLocalFileAsync(localPath, ct);
        _map[localPath] = new CacheItem { Sha256 = sha, FileUri = uploaded.fileUri, MimeType = uploaded.mimeType };
        Persist();
        return uploaded;
    }

    private void Persist()
    {
        var json = JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_storePath, json);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var s = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(s, ct);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}
