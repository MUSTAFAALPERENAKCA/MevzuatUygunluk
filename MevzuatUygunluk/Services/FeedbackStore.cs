using System.Text.Json;
using MevzuatUygunluk.Models;

namespace MevzuatUygunluk.Services;

public class FeedbackStore : IFeedbackStore
{
    private readonly string _path;

    public FeedbackStore(IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "Data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "feedback.json");
    }

    public async Task AddAsync(FeedbackItem item, CancellationToken ct = default)
    {
        var all = await LoadAllAsync(ct);
        all.Add(item);
        var json = JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_path, json, ct);
    }

    public async Task<List<FeedbackItem>> LoadAllAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return new List<FeedbackItem>();
        var json = await File.ReadAllTextAsync(_path, ct);
        return JsonSerializer.Deserialize<List<FeedbackItem>>(json) ?? new List<FeedbackItem>();
    }

    public async Task<List<FeedbackItem>> LoadForAsync(string scenario, string invoiceType, CancellationToken ct = default)
    {
        var all = await LoadAllAsync(ct);
        return all.Where(x =>
                string.Equals(x.Scenario, scenario, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.InvoiceType, invoiceType, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
