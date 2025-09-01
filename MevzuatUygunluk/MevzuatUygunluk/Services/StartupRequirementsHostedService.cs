using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MevzuatUygunluk.Services;

// Uygulama açılışını bloklamayan arka plan servisi
public class StartupRequirementsHostedService : BackgroundService
{
    private readonly ILogger<StartupRequirementsHostedService> _logger;
    private readonly IGeminiService _gemini;
    private readonly IRequirementsStore _store;
    private readonly IConfiguration _cfg;
    private readonly IWebHostEnvironment _env;

    public StartupRequirementsHostedService(
        ILogger<StartupRequirementsHostedService> logger,
        IGeminiService gemini,
        IRequirementsStore store,
        IConfiguration cfg,
        IWebHostEnvironment env)
    {
        _logger = logger;
        _gemini = gemini;
        _store = store;
        _cfg = cfg;
        _env = env;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // hemen dönsün, arka planda çalışsın
        try
        {
            var existing = await _store.LoadAsync(stoppingToken);
            if (existing != null && existing.Requirements.Any())
            {
                _logger.LogInformation("Şartlar mevcut ({Count}). Üretim atlandı.", existing.Requirements.Count);
                return;
            }

            var srcs = _cfg.GetSection("Regulations:Sources").Get<string[]>() ?? Array.Empty<string>();
            if (srcs.Length == 0)
            {
                _logger.LogWarning("Regulations:Sources boş. Otomatik üretim yapılamadı.");
                return;
            }

            var uploaded = new List<(string fileUri, string mimeType)>();
            foreach (var rel in srcs)
            {
                var local = Path.Combine(_env.ContentRootPath, rel.Replace('/', Path.DirectorySeparatorChar));
                _logger.LogInformation("Mevzuat yükleniyor: {Path}", local);
                var up = await _gemini.UploadLocalFileAsync(local, stoppingToken);
                uploaded.Add(up);
            }

            var targetCount = int.TryParse(_cfg["Regulations:RequirementCount"], out var c) ? c : 30;
            var generated = await _gemini.GenerateRequirementsFromSourcesAsync(uploaded, targetCount, stoppingToken);

            await _store.SaveAsync(generated, stoppingToken);
            _logger.LogInformation("Şart üretimi tamamlandı: {Count} madde.", generated.Requirements.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Başlangıç şart üretimi iptal edildi.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Başlangıç şart üretimi başarısız.");
        }
    }
}
