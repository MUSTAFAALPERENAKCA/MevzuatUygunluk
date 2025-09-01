using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;
using MevzuatUygunluk.Models;
using Microsoft.Extensions.Configuration;

namespace MevzuatUygunluk.Services;

public interface IGeminiService
{
    Task<(string fileUri, string mimeType)> UploadLocalFileAsync(string localPath, CancellationToken ct = default);
    Task<GeneratedRequirements> GenerateRequirementsFromRegulationAsync(string fileUri, string mimeType, int targetCount = 20, CancellationToken ct = default);
    Task<GeneratedRequirements> GenerateRequirementsFromSourcesAsync(IEnumerable<(string fileUri, string mimeType)> sources, int targetCount = 30, CancellationToken ct = default);
}

public class GeminiService : IGeminiService
{
    private readonly IHttpClientFactory _factory;
    private readonly IConfiguration _cfg;

    public GeminiService(IHttpClientFactory factory, IConfiguration cfg)
    {
        _factory = factory;
        _cfg = cfg;
    }

    public async Task<(string fileUri, string mimeType)> UploadLocalFileAsync(string localPath, CancellationToken ct = default)
    {
        if (!File.Exists(localPath))
            throw new FileNotFoundException("Mevzuat dosyası bulunamadı.", localPath);

        var apiKey = _cfg["Gemini:ApiKey"] ?? throw new InvalidOperationException("Gemini ApiKey eksik.");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);

        // Dosyadan MIME tahmini (fallback için saklıyoruz)
        var provider = new FileExtensionContentTypeProvider();
        if (!provider.TryGetContentType(localPath, out var guessedMime))
            guessedMime = "application/octet-stream";

        // ---- START ----
        var startReq = new HttpRequestMessage(HttpMethod.Post, "https://generativelanguage.googleapis.com/upload/v1beta/files");
        startReq.Headers.Add("X-Goog-Upload-Protocol", "resumable");
        startReq.Headers.Add("X-Goog-Upload-Command", "start");
        var fi = new FileInfo(localPath);
        startReq.Headers.Add("X-Goog-Upload-Header-Content-Length", fi.Length.ToString());
        startReq.Headers.Add("X-Goog-Upload-Header-Content-Type", guessedMime);
        startReq.Content = new StringContent($"{{\"file\": {{\"display_name\": \"{fi.Name}\"}}}}", Encoding.UTF8, "application/json");

        var startResp = await client.SendAsync(startReq, ct);
        var startBody = await startResp.Content.ReadAsStringAsync(ct);
        if (!startResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Files start failed: {(int)startResp.StatusCode} {startResp.ReasonPhrase}. Body: {startBody}");
        if (!startResp.Headers.TryGetValues("X-Goog-Upload-URL", out var uploadUrls))
            throw new InvalidOperationException($"Upload URL header missing. Response body: {startBody}");
        var uploadUrl = uploadUrls.First();

        // ---- UPLOAD + FINALIZE ----
        var bytes = await File.ReadAllBytesAsync(localPath, ct);
        var uploadReq = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
        uploadReq.Headers.Add("X-Goog-Upload-Offset", "0");
        uploadReq.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
        uploadReq.Content = new ByteArrayContent(bytes);
        uploadReq.Content.Headers.ContentType = new MediaTypeHeaderValue(guessedMime);

        var uploadResp = await client.SendAsync(uploadReq, ct);
        var uploadBody = await uploadResp.Content.ReadAsStringAsync(ct);
        if (!uploadResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Files upload failed: {(int)uploadResp.StatusCode} {uploadResp.ReasonPhrase}. Body: {uploadBody}");

        using var uploadJson = JsonDocument.Parse(uploadBody);
        var root = uploadJson.RootElement;

        // Bazı cevaplarda "file" kökte olur, bazılarında root altında "file" alanı olur
        JsonElement fileEl = root;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("file", out var inner))
            fileEl = inner;

        if (!fileEl.TryGetProperty("uri", out var uriEl) || string.IsNullOrWhiteSpace(uriEl.GetString()))
            throw new InvalidOperationException($"Upload response missing file.uri. Body: {uploadBody}");

        var fileUri = uriEl.GetString()!;

        // mime_type opsiyonel olabilir; yoksa guessedMime kullan
        string mimeType = guessedMime;
        if (fileEl.TryGetProperty("mime_type", out var mtSnake))
        {
            mimeType = mtSnake.GetString() ?? guessedMime;
        }
        else if (fileEl.TryGetProperty("mimeType", out var mtCamel))
        {
            mimeType = mtCamel.GetString() ?? guessedMime;
        }

        return (fileUri, mimeType);
    }

    public async Task<GeneratedRequirements> GenerateRequirementsFromRegulationAsync(string fileUri, string mimeType, int targetCount = 20, CancellationToken ct = default)
        => await GenerateRequirementsFromSourcesAsync(new[] { (fileUri, mimeType) }, targetCount, ct);

    public async Task<GeneratedRequirements> GenerateRequirementsFromSourcesAsync(IEnumerable<(string fileUri, string mimeType)> sources, int targetCount = 30, CancellationToken ct = default)
    {
        var apiKey = _cfg["Gemini:ApiKey"] ?? throw new InvalidOperationException("Gemini ApiKey eksik.");
        var model = _cfg["Gemini:Model"] ?? "gemini-2.0-flash";
        var client = _factory.CreateClient();

        var parts = new List<object>
        {
            new { text = @$"
Aşağıdaki mevzuat dosyalarını incele ve {targetCount} maddelik, net ve ölçülebilir bir 'uygunluk şartları' listesi üret.

Kurallar:
- Her şart tek ve doğrulanabilir bir cümle olsun.
- Varsa mevzuat madde/başlık referansını 'article' alanında belirt.
- Zorunluluk düzeyini 'mustHave' (true/false) ile ver.
- Yalnızca dosya içeriklerine dayan, dış bilgi kullanma.

Yanıtı istediğim JSON şemasına tam uyacak şekilde üret." }
        };

        foreach (var s in sources)
            parts.Add(new { file_data = new { mime_type = s.mimeType, file_uri = s.fileUri } });

        var payload = new
        {
            contents = new[] { new { parts = parts.ToArray() } },
            generationConfig = new
            {
                response_mime_type = "application/json",
                response_schema = new
                {
                    type = "object",
                    properties = new
                    {
                        requirements = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    requirement = new { type = "string" },
                                    article = new { type = "string" },
                                    mustHave = new { type = "boolean" }
                                },
                                required = new[] { "requirement" }
                            }
                        },
                        notes = new { type = "string" }
                    },
                    required = new[] { "requirements" }
                }
            }
        };

        var req = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
        req.Headers.Add("x-goog-api-key", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var resp = await client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Generate failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}");

        using var genDoc = JsonDocument.Parse(body);
        var text = genDoc.RootElement.GetProperty("candidates")[0]
            .GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();

        var result = JsonSerializer.Deserialize<GeneratedRequirements>(text!) ?? new GeneratedRequirements();
        foreach (var r in result.Requirements)
            if (string.IsNullOrWhiteSpace(r.Article)) r.Article = null;

        return result;
    }
}
