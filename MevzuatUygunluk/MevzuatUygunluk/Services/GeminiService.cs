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
        var client = _factory.CreateClient("Gemini");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(10));

        var provider = new FileExtensionContentTypeProvider();
        if (!provider.TryGetContentType(localPath, out var guessedMime))
            guessedMime = "application/octet-stream";

        // ---- START ----
        var startReq = new HttpRequestMessage(HttpMethod.Post, "https://generativelanguage.googleapis.com/upload/v1beta/files");
        startReq.Headers.Add("x-goog-api-key", apiKey);
        startReq.Headers.Add("X-Goog-Upload-Protocol", "resumable");
        startReq.Headers.Add("X-Goog-Upload-Command", "start");
        var fi = new FileInfo(localPath);
        startReq.Headers.Add("X-Goog-Upload-Header-Content-Length", fi.Length.ToString());
        startReq.Headers.Add("X-Goog-Upload-Header-Content-Type", guessedMime);
        startReq.Content = new StringContent($"{{\"file\": {{\"display_name\": \"{fi.Name}\"}}}}", Encoding.UTF8, "application/json");

        var startResp = await client.SendAsync(startReq, cts.Token);
        var startBody = await startResp.Content.ReadAsStringAsync(cts.Token);
        if (!startResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Files start failed: {(int)startResp.StatusCode} {startResp.ReasonPhrase}. Body: {startBody}");
        if (!startResp.Headers.TryGetValues("X-Goog-Upload-URL", out var uploadUrls))
            throw new InvalidOperationException($"Upload URL header missing. Response body: {startBody}");
        var uploadUrl = uploadUrls.First();

        // ---- UPLOAD + FINALIZE ----
        var bytes = await File.ReadAllBytesAsync(localPath, cts.Token);
        var uploadReq = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
        uploadReq.Headers.Add("X-Goog-Upload-Offset", "0");
        uploadReq.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
        uploadReq.Content = new ByteArrayContent(bytes);
        uploadReq.Content.Headers.ContentType = new MediaTypeHeaderValue(guessedMime);

        var uploadResp = await client.SendAsync(uploadReq, cts.Token);
        var uploadBody = await uploadResp.Content.ReadAsStringAsync(cts.Token);
        if (!uploadResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Files upload failed: {(int)uploadResp.StatusCode} {uploadResp.ReasonPhrase}. Body: {uploadBody}");

        using var uploadJson = JsonDocument.Parse(uploadBody);
        var root = uploadJson.RootElement;

        JsonElement fileEl = root;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("file", out var inner))
            fileEl = inner;

        if (!fileEl.TryGetProperty("uri", out var uriEl) || string.IsNullOrWhiteSpace(uriEl.GetString()))
            throw new InvalidOperationException($"Upload response missing file.uri. Body: {uploadBody}");
        var fileUri = uriEl.GetString()!;

        string mimeType = guessedMime;
        if (fileEl.TryGetProperty("mime_type", out var mtSnake))
            mimeType = mtSnake.GetString() ?? guessedMime;
        else if (fileEl.TryGetProperty("mimeType", out var mtCamel))
            mimeType = mtCamel.GetString() ?? guessedMime;

        return (fileUri, mimeType);
    }

    public async Task<GeneratedRequirements> GenerateRequirementsFromRegulationAsync(string fileUri, string mimeType, int targetCount = 20, CancellationToken ct = default)
        => await GenerateRequirementsFromSourcesAsync(new[] { (fileUri, mimeType) }, targetCount, ct);

    public async Task<GeneratedRequirements> GenerateRequirementsFromSourcesAsync(IEnumerable<(string fileUri, string mimeType)> sources, int targetCount = 30, CancellationToken ct = default)
    {
        var apiKey = _cfg["Gemini:ApiKey"] ?? throw new InvalidOperationException("Gemini ApiKey eksik.");
        var model = _cfg["Gemini:Model"] ?? "gemini-2.0-flash";
        var client = _factory.CreateClient("Gemini");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(10));

        // TÜM şartların çıkarımı + kardinalite kuralları
        var parts = new List<object>
        {
            new { text = @"
Aşağıdaki mevzuat dosyalarındaki TÜM uygunluk şartlarını eksiksiz çıkar.

KURALLAR ve HARİTALAMA:
- 'Zorunlu(1)' => mustHave=true, minOccurs=1, maxOccurs=1
- 'Seçimli(0..1)' => mustHave=false, minOccurs=0, maxOccurs=1
- 'Seçimli(0..n)' => mustHave=false, minOccurs=0, maxOccurs=-1   // -1: unbounded
- 'Zorunlu(1..n)' => mustHave=true,  minOccurs=1, maxOccurs=-1
- Alan adını 'Field', bağlam/bölümü 'Section' olarak belirt (örn: PostalAddress.CityName).
- Mümkünse ilgili madde/başlık referansını 'Article' alanına ekle.
- 'Requirement' alanında kısa ve doğrulanabilir tek cümlelik şart yaz.
- ÖZETLEME YAPMA; tespit edilen tüm alanları listele." }
        };

        foreach (var s in sources)
            parts.Add(new { file_data = new { mime_type = s.mimeType, file_uri = s.fileUri } });

        var schema = new
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
                            field = new { type = "string" },
                            section = new { type = "string" },
                            article = new { type = "string" },
                            mustHave = new { type = "boolean" },
                            minOccurs = new { type = "integer" },
                            maxOccurs = new { type = "integer" } // -1 => unbounded
                        },
                        required = new[] { "requirement", "field", "mustHave", "minOccurs", "maxOccurs" }
                    }
                },
                notes = new { type = "string" }
            },
            required = new[] { "requirements" }
        };

        var payload = new
        {
            contents = new[] { new { parts = parts.ToArray() } },
            generationConfig = new
            {
                response_mime_type = "application/json",
                max_output_tokens = 8192,
                response_schema = schema
            }
        };

        var req = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
        req.Headers.Add("x-goog-api-key", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var resp = await client.SendAsync(req, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Generate failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}");

        using var genDoc = JsonDocument.Parse(body);
        var text = genDoc.RootElement.GetProperty("candidates")[0]
            .GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";

        // 1) Normal parse -> 2) Toleranslı çıkar -> 3) Repair çağrısı
        GeneratedRequirements result;
        try
        {
            var cleaned = ExtractFirstJsonLenient(text);
            result = ParseRequirementsJson(cleaned);
        }
        catch
        {
            // REPAIR: modele bozuk çıktıyı verip, geçerli JSON'u şemayla yeniden üretmesini iste
            var repaired = await RepairToValidJsonAsync(text, schema, client, model, apiKey, cts.Token);
            var fixedJson = ExtractFirstJsonLenient(repaired);
            result = ParseRequirementsJson(fixedJson);
        }

        foreach (var r in result.Requirements)
        {
            if (string.IsNullOrWhiteSpace(r.Article)) r.Article = null;
            if (r.MaxOccurs.HasValue && r.MaxOccurs.Value < 0) r.MaxOccurs = -1;
        }

        return result;
    }

    // ----------------- Helpers -----------------

    /// <summary>
    /// Bozuk/eksik JSON metnini modele onartır; yalnızca geçerli JSON döndürmesini ister.
    /// </summary>
    private static async Task<string> RepairToValidJsonAsync(
        string rawText, object schema, HttpClient client, string model, string apiKey, CancellationToken ct)
    {
        var parts = new object[]
        {
            new { text = @"Aşağıda şeması verilen yanıta ait JSON metni bozuk veya kesilmiş olabilir.
LÜTFEN yalnızca şemaya uygun, geçerli bir JSON nesnesi üret. Açıklama, kod bloğu veya ek metin yazma.
Eksik/yarım kalan kayıtlar varsa ATLA. Noktasına virgülüne dikkat et ve tek bir JSON nesnesi döndür." },
            new { text = rawText }
        };

        var payload = new
        {
            contents = new[] { new { parts } },
            generationConfig = new
            {
                response_mime_type = "application/json",
                max_output_tokens = 4096,
                response_schema = schema
            }
        };

        var req = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
        req.Headers.Add("x-goog-api-key", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var resp = await client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Repair failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("candidates")[0]
                 .GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "{}";
    }

    /// <summary>
    /// Metindeki ilk JSON nesne/dizisini çıkarır; kapanış eksikse eksik parantez ve
    /// kesik stringi otomatik tamamlar (en iyi çabayla).
    /// </summary>
    private static string ExtractFirstJsonLenient(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            throw new InvalidOperationException("Model boş yanıt döndürdü.");

        s = s.Trim();

        // code fence temizliği
        if (s.StartsWith("```"))
        {
            var first = s.IndexOf('{');
            var firstArr = s.IndexOf('[');
            var start = first >= 0 && (first < firstArr || firstArr < 0) ? first : firstArr;
            if (start >= 0) s = s.Substring(start);
        }

        int idxObj = s.IndexOf('{');
        int idxArr = s.IndexOf('[');
        int startIdx;
        if (idxObj >= 0 && (idxObj < idxArr || idxArr < 0)) startIdx = idxObj;
        else if (idxArr >= 0) startIdx = idxArr;
        else throw new InvalidOperationException("Yanıtta JSON bulunamadı.");

        var stack = new Stack<char>();
        bool inStr = false, esc = false;
        int iEnd = -1;

        for (int i = startIdx; i < s.Length; i++)
        {
            char c = s[i];

            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
            }
            else
            {
                if (c == '"') inStr = true;
                else if (c == '{' || c == '[') stack.Push(c);
                else if (c == '}' || c == ']')
                {
                    if (stack.Count == 0) break;
                    var open = stack.Pop();
                    if ((open == '{' && c != '}') || (open == '[' && c != ']')) break;
                    if (stack.Count == 0) { iEnd = i; break; }
                }
            }
        }

        if (iEnd >= 0)
            return s.Substring(startIdx, iEnd - startIdx + 1).Trim();

        // JSON kesilmiş; elden geldiğince kapat
        var frag = s.Substring(startIdx).TrimEnd();

        // Eğer string içinde kesildiyse kapatma denemesi için son kapatmayan
        // stringe bir çift tırnak ekleyelim (en iyi çaba)
        if (inStr)
            frag += "\"";

        // Açık parantezleri say ve kapat
        stack.Clear(); inStr = false; esc = false;
        foreach (var ch in frag)
        {
            if (inStr)
            {
                if (esc) esc = false;
                else if (ch == '\\') esc = true;
                else if (ch == '"') inStr = false;
                continue;
            }

            if (ch == '"') { inStr = true; continue; }
            if (ch == '{' || ch == '[') stack.Push(ch);
            else if (ch == '}' || ch == ']')
            {
                if (stack.Count == 0) break;
                var open = stack.Pop();
                if ((open == '{' && ch != '}') || (open == '[' && ch != ']')) break;
            }
        }

        var sb = new StringBuilder(frag);
        while (stack.Count > 0)
        {
            var open = stack.Pop();
            sb.Append(open == '{' ? '}' : ']');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Hem { requirements:[...] } hem de doğrudan [...] formatını destekleyen parse.
    /// </summary>
    private static GeneratedRequirements ParseRequirementsJson(string json)
    {
        try
        {
            var obj = JsonSerializer.Deserialize<GeneratedRequirements>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (obj?.Requirements?.Count > 0) return obj!;
        }
        catch { }

        try
        {
            var arr = JsonSerializer.Deserialize<List<RequirementItem>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (arr != null) return new GeneratedRequirements { Requirements = arr };
        }
        catch { }

        var preview = json.Length > 500 ? json[..500] + "..." : json;
        throw new InvalidOperationException("Modelden gelen JSON beklenen şemaya uymuyor. Örnek: " + preview);
    }
}
