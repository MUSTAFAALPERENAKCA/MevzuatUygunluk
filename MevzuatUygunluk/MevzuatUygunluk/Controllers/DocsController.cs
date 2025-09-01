using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MevzuatUygunluk.Models;
using MevzuatUygunluk.Services;

namespace MevzuatUygunluk.Controllers
{
    public class DocsController : Controller
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _config;
        private readonly IRequirementsStore _reqStore;
        private readonly IGeminiService _gemini;
        private readonly IWebHostEnvironment _env;

        public DocsController(
            IHttpClientFactory httpFactory,
            IConfiguration config,
            IRequirementsStore reqStore,
            IGeminiService gemini,
            IWebHostEnvironment env)
        {
            _httpFactory = httpFactory;
            _config = config;
            _reqStore = reqStore;
            _gemini = gemini;
            _env = env;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var reqs = await EnsureRequirementsAsync();
            ViewBag.Requirements = reqs;
            return View(new ChecksVm());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AnalyzeChecks(IFormFile file)
        {
            var vm = new ChecksVm();

            if (file is null || file.Length == 0)
            {
                vm.Error = "Dosya yüklenmedi.";
                return await ReturnIndexWithReqs(vm);
            }

            try
            {
                var apiKey = _config["Gemini:ApiKey"];
                if (string.IsNullOrWhiteSpace(apiKey))
                    throw new InvalidOperationException("Gemini ApiKey eksik. (User Secrets: Gemini:ApiKey)");

                var model = _config["Gemini:Model"] ?? "gemini-2.0-flash";

                var client = _httpFactory.CreateClient();
                client.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);

                // 1) START
                var startReq = new HttpRequestMessage(HttpMethod.Post, "https://generativelanguage.googleapis.com/upload/v1beta/files");
                startReq.Headers.Add("X-Goog-Upload-Protocol", "resumable");
                startReq.Headers.Add("X-Goog-Upload-Command", "start");
                startReq.Headers.Add("X-Goog-Upload-Header-Content-Length", file.Length.ToString());
                startReq.Headers.Add("X-Goog-Upload-Header-Content-Type", file.ContentType ?? "application/octet-stream");
                startReq.Content = new StringContent("{\"file\": {\"display_name\": \"" + (file.FileName ?? "upload") + "\"}}", Encoding.UTF8, "application/json");

                var startResp = await client.SendAsync(startReq);
                var startBody = await startResp.Content.ReadAsStringAsync();
                if (!startResp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Files start failed: {(int)startResp.StatusCode} {startResp.ReasonPhrase}. Body: {startBody}");
                if (!startResp.Headers.TryGetValues("X-Goog-Upload-URL", out var uploadUrls))
                    throw new InvalidOperationException($"Upload URL header missing. Response body: {startBody}");
                var uploadUrl = uploadUrls.First();

                // 2) UPLOAD + FINALIZE
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                ms.Position = 0;

                var uploadReq = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
                uploadReq.Headers.Add("X-Goog-Upload-Offset", "0");
                uploadReq.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
                uploadReq.Content = new ByteArrayContent(ms.ToArray());
                uploadReq.Content.Headers.ContentLength = ms.Length;
                uploadReq.Content.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");

                var uploadResp = await client.SendAsync(uploadReq);
                var uploadBody = await uploadResp.Content.ReadAsStringAsync();
                if (!uploadResp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Files upload failed: {(int)uploadResp.StatusCode} {uploadResp.ReasonPhrase}. Body: {uploadBody}");

                using var uploadJson = new MemoryStream(Encoding.UTF8.GetBytes(uploadBody));
                using var doc = await JsonDocument.ParseAsync(uploadJson);
                var fileObj = doc.RootElement.GetProperty("file");
                var fileUri = fileObj.GetProperty("uri").GetString();
                var mimeType = fileObj.TryGetProperty("mime_type", out var mt)
                    ? (mt.GetString() ?? "application/octet-stream")
                    : (file.ContentType ?? "application/octet-stream");

                if (string.IsNullOrWhiteSpace(fileUri))
                    throw new InvalidOperationException("file.uri alýnamadý.");

                // 3) ÞART LÝSTESÝ — DAÝMA MEVZUATTAN ÜRET (gerekirse þimdi üret)
                var reqs = await EnsureRequirementsAsync();

                // 4) PROMPT
                var prompt =
@"Aþaðýdaki dosyayý yalnýzca içeriðindeki kanýtlara dayanarak deðerlendir.
Her bir þart için JSON alanlarý döndür:
- requirement (string)
- present (boolean)
- evidence (string, max 300 karakter, metinden alýntý)
- pages (integer list, mümkünse)
- confidence (0-1)

Kanýt bulunamazsa present=false ve evidence='-'.

Þartlar:
" + string.Join("\n- ", reqs.Select(r => "- " + r));

                // 5) GENERATE (structured JSON)
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
                                },
                                overall_summary = new { type = "string" }
                            },
                            required = new[] { "checks" }
                        }
                    }
                };

                var genReq = new HttpRequestMessage(HttpMethod.Post, genUrl);
                genReq.Headers.Add("x-goog-api-key", _config["Gemini:ApiKey"]);
                genReq.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var genResp = await client.SendAsync(genReq);
                var genBody = await genResp.Content.ReadAsStringAsync();
                if (!genResp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Generate failed: {(int)genResp.StatusCode} {genResp.ReasonPhrase}. Body: {genBody}");

                using var genDoc = JsonDocument.Parse(genBody);
                string? jsonText = null;
                if (genDoc.RootElement.TryGetProperty("candidates", out var candArr) && candArr.GetArrayLength() > 0)
                {
                    var cand0 = candArr[0];
                    if (cand0.TryGetProperty("content", out var content) &&
                        content.TryGetProperty("parts", out var parts) &&
                        parts.GetArrayLength() > 0 &&
                        parts[0].TryGetProperty("text", out var textEl))
                    {
                        jsonText = textEl.GetString();
                    }
                }
                if (string.IsNullOrWhiteSpace(jsonText))
                    throw new InvalidOperationException($"Yapýlandýrýlmýþ yanýt alýnamadý. Body: {genBody}");

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

            return await ReturnIndexWithReqs(vm);
        }

        private async Task<IActionResult> ReturnIndexWithReqs(ChecksVm vm)
        {
            var reqs = await EnsureRequirementsAsync();
            ViewBag.Requirements = reqs;
            return View("Index", vm);
        }

        /// <summary>
        /// Þartlarý store'dan yükler; yoksa 'Regulations:Sources' içindeki mevzuat dosyalarýndan üretip kaydeder.
        /// Her zaman güncel þart listesini string olarak döner.
        /// </summary>
        private async Task<List<string>> EnsureRequirementsAsync(CancellationToken ct = default)
        {
            var gen = await _reqStore.LoadAsync(ct);
            if (gen != null && gen.Requirements?.Any() == true)
                return gen.Requirements.Select(x => x.Requirement).ToList();

            var rels = _config.GetSection("Regulations:Sources").Get<string[]>() ?? Array.Empty<string>();
            if (rels.Length == 0)
                return RequirementsCatalog.Default.ToList(); // kaynak yoksa son çare

            var uploaded = new List<(string fileUri, string mimeType)>();
            foreach (var rel in rels)
            {
                var local = Path.Combine(_env.ContentRootPath, rel.Replace('/', Path.DirectorySeparatorChar));
                var up = await _gemini.UploadLocalFileAsync(local, ct);
                uploaded.Add(up);
            }

            var count = int.TryParse(_config["Regulations:RequirementCount"], out var c) ? c : 30;
            var generated = await _gemini.GenerateRequirementsFromSourcesAsync(uploaded, count, ct);
            await _reqStore.SaveAsync(generated, ct);

            return generated.Requirements.Select(x => x.Requirement).ToList();
        }
    }
}
