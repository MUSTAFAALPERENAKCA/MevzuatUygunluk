using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

public class DocsController : Controller
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;

    public DocsController(IHttpClientFactory httpFactory, IConfiguration config)
    {
        _httpFactory = httpFactory;
        _config = config;
    }

    [HttpGet]
    public IActionResult Index() => View(new ChecksVm());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AnalyzeChecks(IFormFile file)
    {
        var vm = new ChecksVm();

        if (file == null || file.Length == 0)
        {
            vm.Error = "Dosya yüklenmedi.";
            return View("Index", vm);
        }

        try
        {
            var apiKey = _config["Gemini:ApiKey"] ?? throw new InvalidOperationException("Gemini ApiKey eksik.");
            var model = _config["Gemini:Model"] ?? "gemini-2.0-flash";

            var client = _httpFactory.CreateClient();
            client.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);

            // 1) Resumable upload: start
            var startReq = new HttpRequestMessage(HttpMethod.Post,
                "https://generativelanguage.googleapis.com/upload/v1beta/files");
            startReq.Headers.Add("X-Goog-Upload-Protocol", "resumable");
            startReq.Headers.Add("X-Goog-Upload-Command", "start");
            startReq.Headers.Add("X-Goog-Upload-Header-Content-Length", file.Length.ToString());
            startReq.Headers.Add("X-Goog-Upload-Header-Content-Type", file.ContentType ?? "application/octet-stream");
            startReq.Content = new StringContent(
                "{\"file\": {\"display_name\": \"" + (file.FileName ?? "upload") + "\"}}",
                Encoding.UTF8, "application/json");

            var startResp = await client.SendAsync(startReq);
            startResp.EnsureSuccessStatusCode();
            var uploadUrl = startResp.Headers.GetValues("X-Goog-Upload-URL").First();

            // 2) Upload + finalize
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            ms.Position = 0;
            var uploadReq = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
            uploadReq.Headers.Add("X-Goog-Upload-Offset", "0");
            uploadReq.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
            uploadReq.Content = new ByteArrayContent(ms.ToArray());
            uploadReq.Content.Headers.ContentLength = ms.Length;
            uploadReq.Content.Headers.ContentType =
                new MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");

            var uploadResp = await client.SendAsync(uploadReq);
            uploadResp.EnsureSuccessStatusCode();
            using var uploadJson = await uploadResp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(uploadJson);
            var fileObj = doc.RootElement.GetProperty("file");
            var fileUri = fileObj.GetProperty("uri").GetString();
            var mimeType = fileObj.GetProperty("mime_type").GetString() ?? "application/octet-stream";

            // 3) Þartlarý katalogdan çek
            var reqs = RequirementsCatalog.Default;

            // 4) Prompt
            var prompt =
@"Aþaðýdaki dosyayý deðerlendir.
Her þart için JSON döndür:
- requirement
- present
- evidence
- pages
- confidence

Þartlar:
" + string.Join("\n- ", reqs);

            // 5) Structured JSON iste
            var genUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";
            var payload = new
            {
                contents = new[]
                {
                    new {
                        parts = new object[]
                        {
                            new { text = prompt },
                            new { file_data = new { mime_type = mimeType, file_uri = fileUri } }
                        }
                    }
                },
                generationConfig = new
                {
                    response_mime_type = "application/json",
                    response_schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            checks = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        requirement = new { type = "string" },
                                        present = new { type = "boolean" },
                                        evidence = new { type = "string" },
                                        pages = new { type = "array", items = new { type = "integer" } },
                                        confidence = new { type = "number" }
                                    },
                                    required = new[] { "requirement", "present" }
                                }
                            }
                        },
                        required = new[] { "checks" }
                    }
                }
            };

            var genReq = new HttpRequestMessage(HttpMethod.Post, genUrl);
            genReq.Headers.Add("x-goog-api-key", apiKey);
            genReq.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var genResp = await client.SendAsync(genReq);
            genResp.EnsureSuccessStatusCode();
            var genJson = await genResp.Content.ReadAsStringAsync();

            using var genDoc = JsonDocument.Parse(genJson);
            var jsonText = genDoc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            };
            var parsed = JsonSerializer.Deserialize<GeminiChecksResponse>(jsonText!, opts);

            vm.Checks = parsed?.Checks ?? new List<CheckResult>();
        }
        catch (Exception ex)
        {
            vm.Error = ex.Message;
        }

        return View("Index", vm);
    }
}
