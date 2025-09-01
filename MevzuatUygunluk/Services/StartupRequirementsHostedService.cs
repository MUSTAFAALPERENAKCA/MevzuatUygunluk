using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MevzuatUygunluk.Services;

public class StartupRequirementsHostedService : IHostedService
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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var existing = await _store.LoadAsync(cancellationToken);
            if (existing != null && existing.Requirements.Any())
            {
                _logger.LogInformation("Şartlar mevcut ({Count}). Otomatik üretim atlandı.", existing.Requirements.Count);
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
                var path = Path.Combine(_env.ContentRootPath, rel.Replace('/', Path.DirectorySeparatorChar));
                _logger.LogInformation("Mevzuat yükleniyor: {Path}", path);
                var up = await _gemini.UploadLocalFileAsync(path, cancellationToken);
                uploaded.Add(up);
            }

            var targetCount = int.TryParse(_cfg["Regulations:RequirementCount"], out var c) ? c : 30;
            var generated = await _gemini.GenerateRequirementsFromSourcesAsync(uploaded, targetCount, cancellationToken);

            await _store.SaveAsync(generated, cancellationToken);
            _logger.LogInformation("Şart üretimi tamam: {Count} madde kaydedildi ({StorePath})",
                generated.Requirements.Count, _store.StorePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Başlangıç şart üretimi başarısız.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
