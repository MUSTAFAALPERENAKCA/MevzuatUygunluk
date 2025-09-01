using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using MevzuatUygunluk.Models;
using MevzuatUygunluk.Services;

public class DocsController : Controller
{
    private const string SessionKey = "LAST_CHECKS_JSON";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly IRequirementsStore _store;
    private readonly IFeedbackStore _feedbacks;
    private readonly IRegulationUploadCache _regCache;
    private readonly IGeminiService _gemini;
    private readonly IWebHostEnvironment _env;

    public DocsController(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        IRequirementsStore store,
        IFeedbackStore feedbacks,
        IRegulationUploadCache regCache,
        IGeminiService gemini,
        IWebHostEnvironment env)
    {
        _httpFactory = httpFactory;
        _config = config;
        _store = store;
        _feedbacks = feedbacks;
        _regCache = regCache;
        _gemini = gemini;
        _env = env;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var vm = BuildVmDefaults();
        var reqs = await _store.LoadAsync();
        ViewBag.ReqCount = reqs?.Requirements?.Count ?? 0;
        return View(vm);
    }

    private ChecksVm BuildVmDefaults()
    {
        return new ChecksVm
        {
            ScenarioOptions = Enum.GetValues<InvoiceScenario>()
                .Select(x => new SelectListItem(x.ToString(), x.ToString())).ToList(),
            TypeOptions = Enum.GetValues<InvoiceType>()
                .Select(x => new SelectListItem(x.ToString(), x.ToString())).ToList()
        };
    }

    // =========================================================
    //  ANALYZE (çoklu dosya + zip + dayanak + feedback + sağlam JSON + RETRY)
    // =========================================================
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AnalyzeChecks(ChecksVm model, CancellationToken ct)
    {
        var vm = BuildVmDefaults();
        vm.Scenario = model.Scenario;
        vm.InvoiceType = model.InvoiceType;

        var uploads = new List<IFormFile>();
        if (model.File != null) uploads.Add(model.File);
        if (model.Files != null && model.Files.Count > 0) uploads.AddRange(model.Files);

        if (uploads.Count == 0)
        {
            vm.Error = "Lütfen en az bir dosya seçiniz. (PDF/Word/Resim veya .zip)";
            return View("Index", vm);
        }

        try
        {
            // 1) Zip aç / diske yaz
            var localPaths = await MaterializeUploadsAsync(uploads, ct); // (path, fileName, mime)

            // 2) Kullanıcı dosyalarını Gemini'ye yükle (cache GeminiService içinde)
            var uploadedUserFiles = new List<(string fileName, string fileUri, string mime)>();
            foreach (var u in localPaths)
            {
                var up = await _gemini.UploadLocalFileAsync(u.path, ct);
                uploadedUserFiles.Add((u.fileName, up.fileUri, up.mimeType));
            }

            // 3) Mevzuat dosyaları
            var lawAPath = Path.Combine(_env.ContentRootPath, "Docs", "UBL-TR Ortak Elemanlar - V 0.7.pdf");
            var lawBPath = Path.Combine(_env.ContentRootPath, "Docs", "1.4.213.pdf");
            var lawA = await _regCache.GetOrUploadAsync(lawAPath, _gemini, ct);
            var lawB = await _regCache.GetOrUploadAsync(lawBPath, _gemini, ct);

            // 4) Şart listesi + sözlük
            var generated = await _store.LoadAsync(ct);
            var reqList = (generated?.Requirements ?? new List<RequirementItem>())
                .Select(r => new RequirementLite
                {
                    Requirement = r.Requirement ?? "",
                    MustHave = r.MustHave,
                    MinOccurs = r.MinOccurs ?? 0,
                    MaxOccurs = r.MaxOccurs ?? -1
                }).ToList();

            var reqDict = reqList
                .GroupBy(r => Normalize(r.Requirement))
                .ToDictionary(g => g.Key, g => g.First().MustHave);

            var reqs = reqList.Select(r => new
            {
                requirement = r.Requirement,
                mustHave = r.MustHave,
                minOccurs = r.MinOccurs,
                maxOccurs = r.MaxOccurs
            }).ToArray();

            var hints = await _feedbacks.LoadForAsync(model.Scenario.ToString(), model.InvoiceType.ToString(), ct);

            // 5) Prompt (kanıt/sayfa/güven asla boş kalmasın)
            var scenarioTxt = model.Scenario.ToString();
            var typeTxt = model.InvoiceType.ToString();

            var prompt = @$"
Senaryo = {scenarioTxt}, FaturaTipi = {typeTxt}.
Aşağıda user_files ve mevzuat dokümanları sağlanır. Her şart için:
- Dosya bazında kanıtları çıkar (evidence_by_file) ve genel sonucu (present) belirle.
- Mevzuat dayanağını (law_refs) döndür.
- JSON'u kesinlikle geçerli üret; asla kapanmayan dizi/obje bırakma; trailing comma koyma.
- **present=true** ise 'evidence' alanını kısa ve insan-dostu bir özet (alan adı/değer veya 1-2 kelimelik alıntı) ile DOLDUR; boş bırakma.
- 'pages' alanına kanıtın görüldüğü 1-bazlı sayfa numaralarını yaz (bilinmiyorsa [] bırak).
- 'confidence' 0..1 aralığında olsun; bilinmiyorsa 0.5 koy.
- 'evidence_by_file' içindeki kanıt ve sayfalar ile 'evidence' ve 'pages' tutarlı olsun.

user_files:
{JsonSerializer.Serialize(uploadedUserFiles.Select(x => new { name = x.fileName, mime = x.mime }), new JsonSerializerOptions { WriteIndented = true })}

Önceki kullanıcı düzeltme ipuçları (varsa dikkate al):
{JsonSerializer.Serialize(hints.Select(h => new { requirementKey = h.RequirementKey, present = h.PresentOverride, evidence = h.EvidenceOverride }), new JsonSerializerOptions { WriteIndented = true })}

Yalnız ilgili şartları döndür. JSON dışına çıkma.";

            // 6) GenerateContent (RETRY ile)
            var apiKey = _config["Gemini:ApiKey"] ?? throw new InvalidOperationException("Gemini ApiKey eksik.");
            var modelName = _config["Gemini:Model"] ?? "gemini-2.0-flash";
            var http = _httpFactory.CreateClient("Gemini");

            var genUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent";

            var parts = new List<object>
            {
                new { text = prompt },
                new { text = JsonSerializer.Serialize(new { requirements = reqs }) }
            };
            foreach (var f in uploadedUserFiles)
                parts.Add(new { file_data = new { mime_type = f.mime, file_uri = f.fileUri } });
            parts.Add(new { file_data = new { mime_type = lawA.mimeType, file_uri = lawA.fileUri } });
            parts.Add(new { file_data = new { mime_type = lawB.mimeType, file_uri = lawB.fileUri } });

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
                                        confidence = new { type = "number" },
                                        mustHave = new { type = "boolean" },
                                        evidence_by_file = new
                                        {
                                            type = "array",
                                            items = new
                                            {
                                                type = "object",
                                                properties = new
                                                {
                                                    file = new { type = "string" },
                                                    present = new { type = "boolean" },
                                                    evidence = new { type = "string" },
                                                    pages = new { type = "array", items = new { type = "integer" } },
                                                    confidence = new { type = "number" }
                                                }
                                            }
                                        },
                                        law_refs = new
                                        {
                                            type = "array",
                                            items = new
                                            {
                                                type = "object",
                                                properties = new
                                                {
                                                    doc_name = new { type = "string" },
                                                    page = new { type = "integer" },
                                                    quote = new { type = "string" }
                                                }
                                            }
                                        }
                                    },
                                    required = new[] { "requirement", "present" }
                                }
                            }
                        },
                        required = new[] { "checks" }
                    }
                }
            };

            var payloadJson = JsonSerializer.Serialize(payload);

            // --- RETRY + BACKOFF ile çağır ---
            var (resp, body) = await PostJsonWithRetriesAsync(http, genUrl, apiKey, payloadJson, ct);

            // 7) Güvenli JSON çıkar + dezenfekte + deserialize
            using var genDoc = JsonDocument.Parse(body);
            var rawText = genDoc.RootElement.GetProperty("candidates")[0]
                .GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "{}";

            var jsonStr = SanitizeJson(ExtractFirstJsonLenient(rawText));
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };
            var parsed = JsonSerializer.Deserialize<GeminiChecksResponse_Expanded>(jsonStr, opts);

            var checks = parsed?.Checks ?? new List<CheckResult>();

            // 8) Normalizasyon + TOPLAMA (kanıt/sayfa/güven boş kalmasın)
            foreach (var c in checks)
            {
                // pages normalize
                c.Pages = (c.Pages ?? Array.Empty<int>()).Where(p => p >= 0).Distinct().OrderBy(p => p).Take(200).ToArray();

                // mustHave metin ipucu -> yoksa store sözlüğü
                var txt = (c.Requirement ?? "").ToLowerInvariant();
                bool? fromText = null;
                if (txt.Contains("mandatory") || txt.Contains("zorunlu")) fromText = true;
                else if (txt.Contains("optional") || txt.Contains("opsiyonel") || txt.Contains("seçimli") || txt.Contains("secimli")) fromText = false;

                if (fromText.HasValue) c.MustHave = fromText.Value;
                else
                {
                    var key = Normalize(c.Requirement ?? "");
                    if (reqDict.TryGetValue(key, out var must)) c.MustHave = must;
                }

                // kanıt/sayfa/güven boşsa evidence_by_file'dan türet
                c.EvidenceByFile ??= new List<FileEvidence>();
                c.LawRefs ??= new List<LawRef>();

                if (string.IsNullOrWhiteSpace(c.Evidence))
                {
                    var snippets = c.EvidenceByFile
                        .Select(ebf => string.IsNullOrWhiteSpace(ebf.Evidence) ? null :
                            $"{(string.IsNullOrEmpty(ebf.FileName) ? "dosya" : ebf.FileName)}: {Truncate(ebf.Evidence, 80)}")
                        .Where(s => s != null)
                        .Take(3);
                    c.Evidence = string.Join(" | ", snippets!);
                }

                if (c.Pages.Length == 0)
                {
                    var unionPages = c.EvidenceByFile
                        .SelectMany(ebf => ebf.Pages ?? Array.Empty<int>())
                        .Where(p => p >= 0)
                        .Distinct()
                        .OrderBy(p => p)
                        .Take(200)
                        .ToArray();
                    if (unionPages.Length > 0) c.Pages = unionPages;
                }

                if (!c.Confidence.HasValue || double.IsNaN(c.Confidence.Value))
                {
                    var confs = c.EvidenceByFile.Where(e => e.Confidence.HasValue).Select(e => e.Confidence!.Value).ToList();
                    if (confs.Count > 0) c.Confidence = Math.Round(confs.Average(), 2);
                    else c.Confidence = 0.5; // varsayılan
                }
            }

            // 9) Kullanıcı düzeltmeleri (son söz kullanıcıda)
            var fb = await _feedbacks.LoadForAsync(model.Scenario.ToString(), model.InvoiceType.ToString(), ct);
            var map = fb.ToDictionary(x => x.RequirementKey, x => x);
            foreach (var c in checks)
            {
                var key = Normalize(c.Requirement ?? "");
                if (map.TryGetValue(key, out var fix))
                {
                    if (fix.PresentOverride.HasValue) c.Present = fix.PresentOverride.Value;
                    if (!string.IsNullOrWhiteSpace(fix.EvidenceOverride)) c.Evidence = fix.EvidenceOverride;
                }
            }

            vm.Checks = checks;

            HttpContext.Session.SetString(SessionKey, JsonSerializer.Serialize(new
            {
                scenario = vm.Scenario.ToString(),
                invoiceType = vm.InvoiceType.ToString(),
                checks = vm.Checks
            }));
        }
        catch (Exception ex)
        {
            vm.Error = ex.Message;
        }

        return View("Index", vm);
    }

    // =========================================================
    //  FEEDBACK (HITL)
    // =========================================================
    public class FeedbackDto
    {
        public string Requirement { get; set; } = "";
        public bool? Present { get; set; }
        public string? Evidence { get; set; }
        public string Scenario { get; set; } = "";
        public string InvoiceType { get; set; } = "";
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveFeedback([FromBody] FeedbackDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Requirement)) return BadRequest("Requirement boş olamaz.");

        var item = new FeedbackItem
        {
            RequirementKey = Normalize(dto.Requirement),
            Scenario = dto.Scenario,
            InvoiceType = dto.InvoiceType,
            PresentOverride = dto.Present,
            EvidenceOverride = dto.Evidence
        };
        await _feedbacks.AddAsync(item, ct);
        return Ok(new { ok = true });
    }

    // =========================================================
    //  EXPORT
    // =========================================================
    [HttpGet]
    public IActionResult ExportExcel()
    {
        var json = HttpContext.Session.GetString(SessionKey);
        if (string.IsNullOrEmpty(json)) return BadRequest("Önce denetim yapın.");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var checks = JsonSerializer.Deserialize<List<CheckResult>>(root.GetProperty("checks").GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sonuclar");
        ws.Cell(1, 1).Value = "Şart"; ws.Cell(1, 2).Value = "Zorunlu"; ws.Cell(1, 3).Value = "Durum";
        ws.Cell(1, 4).Value = "Kanıt"; ws.Cell(1, 5).Value = "Sayfalar"; ws.Cell(1, 6).Value = "Güven";
        ws.Range(1, 1, 1, 6).Style.Font.Bold = true;

        for (int i = 0; i < checks.Count; i++)
        {
            var c = checks[i];
            ws.Cell(i + 2, 1).Value = c.Requirement;
            ws.Cell(i + 2, 2).Value = c.MustHave ? "Evet" : "Hayır";
            ws.Cell(i + 2, 3).Value = c.Present ? "Var" : "Yok";
            ws.Cell(i + 2, 4).Value = c.Evidence ?? "";
            ws.Cell(i + 2, 5).Value = c.Pages == null ? "" : string.Join(",", c.Pages);
            ws.Cell(i + 2, 6).Value = c.Confidence;
        }
        ws.Columns().AdjustToContents();
        using var ms = new MemoryStream(); wb.SaveAs(ms); ms.Position = 0;
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "DenetimSonuclari.xlsx");
    }

    [HttpGet]
    public IActionResult ExportPdf()
    {
        var json = HttpContext.Session.GetString(SessionKey);
        if (string.IsNullOrEmpty(json)) return BadRequest("Önce denetim yapın.");

        var payload = JsonSerializer.Deserialize<ExportPayload>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ExportPayload();

        QuestPDF.Settings.License = LicenseType.Community;

        var bytes = Document.Create(c =>
        {
            c.Page(p =>
            {
                p.Margin(30);
                p.Header().Text($"Belge Denetim Sonuçları - {payload.Scenario} / {payload.InvoiceType}")
                    .SemiBold().FontSize(16).AlignCenter();
                p.Content().Table(t =>
                {
                    t.ColumnsDefinition(cols =>
                    { cols.RelativeColumn(4); cols.RelativeColumn(1); cols.RelativeColumn(1); cols.RelativeColumn(4); });

                    t.Cell().BorderBottom(1).Padding(5).Text("Şart").SemiBold();
                    t.Cell().BorderBottom(1).Padding(5).Text("Zorunlu").SemiBold();
                    t.Cell().BorderBottom(1).Padding(5).Text("Durum").SemiBold();
                    t.Cell().BorderBottom(1).Padding(5).Text("Kanıt").SemiBold();

                    foreach (var c in payload.Checks)
                    {
                        var isErr = c.MustHave && !c.Present;
                        t.Cell().Background(isErr ? Colors.Red.Lighten4 : Colors.White).Padding(4).Text(c.Requirement);
                        t.Cell().Background(isErr ? Colors.Red.Lighten4 : Colors.White).Padding(4).Text(c.MustHave ? "Evet" : "Hayır");
                        t.Cell().Background(isErr ? Colors.Red.Lighten4 : Colors.White).Padding(4).Text(c.Present ? "Var" : "Yok");
                        t.Cell().Background(isErr ? Colors.Red.Lighten4 : Colors.White).Padding(4).Text(c.Evidence ?? "-");
                    }
                });
                p.Footer().AlignRight().Text(x => { x.Span("Oluşturma: "); x.Span(DateTime.Now.ToString("dd.MM.yyyy HH:mm")); });
            });
        }).GeneratePdf();

        return File(bytes, "application/pdf", "DenetimSonuclari.pdf");
    }

    // =========================================================
    //  Helpers
    // =========================================================
    private class ExportPayload
    {
        public string? Scenario { get; set; }
        public string? InvoiceType { get; set; }
        public List<CheckResult> Checks { get; set; } = new();
    }

    private sealed class RequirementLite
    {
        public string Requirement { get; set; } = "";
        public bool MustHave { get; set; }
        public int MinOccurs { get; set; }
        public int MaxOccurs { get; set; }
    }

    private static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.ToLowerInvariant().Trim();
        var arr = s.Where(ch => !char.IsPunctuation(ch)).ToArray();
        var noPunc = new string(arr);
        return string.Join(' ', noPunc.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");

    private async Task<List<(string path, string fileName, string mime)>> MaterializeUploadsAsync(List<IFormFile> inputs, CancellationToken ct)
    {
        var list = new List<(string, string, string)>();
        var root = Path.Combine(_env.ContentRootPath, "Data", "Uploads");
        Directory.CreateDirectory(root);

        foreach (var f in inputs)
        {
            var ext = Path.GetExtension(f.FileName).ToLowerInvariant();
            if (ext == ".zip")
            {
                using var ms = new MemoryStream();
                await f.CopyToAsync(ms, ct);
                ms.Position = 0;
                using var za = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
                foreach (var e in za.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;
                    var innerExt = Path.GetExtension(e.Name).ToLowerInvariant();
                    if (!IsAllowed(innerExt)) continue;
                    var outPath = Path.Combine(root, Guid.NewGuid().ToString("N") + innerExt);
                    using var es = e.Open();
                    await using var fs = System.IO.File.Create(outPath);
                    await es.CopyToAsync(fs, ct);
                    list.Add((outPath, e.Name, MimeFromExt(innerExt)));
                }
            }
            else
            {
                if (!IsAllowed(ext)) continue;
                var outPath = Path.Combine(root, Guid.NewGuid().ToString("N") + ext);
                await using var fs = System.IO.File.Create(outPath);
                await f.CopyToAsync(fs, ct);
                list.Add((outPath, f.FileName, MimeFromExt(ext)));
            }
        }
        return list;
    }

    private static bool IsAllowed(string ext)
        => new[] { ".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".doc", ".docx", ".xml" }.Contains(ext);

    private static string MimeFromExt(string ext) => ext switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".tif" or ".tiff" => "image/tiff",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xml" => "application/xml",
        _ => "application/octet-stream"
    };

    // ---- JSON çıkarma & dezenfeksiyon ----
    private static string ExtractFirstJsonLenient(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "{}";

        s = s.Replace("\r", "");
        s = Regex.Replace(s, @"^\s*```(?:json)?", "", RegexOptions.Multiline);
        s = Regex.Replace(s, @"```$", "", RegexOptions.Multiline).Trim();

        int start = s.IndexOf('{');
        if (start < 0) return "{}";

        int brace = 0;
        var sb = new StringBuilder();
        bool inStr = false;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            sb.Append(c);
            if (c == '"' && (i == start || s[i - 1] != '\\')) inStr = !inStr;
            if (inStr) continue;
            if (c == '{') brace++;
            else if (c == '}')
            {
                brace--;
                if (brace == 0)
                    break;
            }
        }
        while (brace-- > 0) sb.Append('}');
        return sb.ToString();
    }

    private static string SanitizeJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        json = json.Trim('\uFEFF', '\u200B', '\u2060', '\u0000', '\t', '\n', '\r', ' ');
        json = Regex.Replace(json, @",\s*([\]}])", "$1");
        json = Regex.Replace(json, @"\bNaN\b|\bInfinity\b|-Infinity", "null");
        json = Regex.Replace(json, "\"((?:[^\"\\\\]|\\\\.)*)\"", m =>
        {
            var inner = m.Groups[1].Value.Replace("\n", "\\n").Replace("\r", "\\r");
            return "\"" + inner + "\"";
        });
        return json;
    }

    // ---- 503/429 için RETRY + BACKOFF ----
    private static async Task<(HttpResponseMessage resp, string body)> PostJsonWithRetriesAsync(
        HttpClient http, string url, string apiKey, string payloadJson, CancellationToken ct,
        int maxAttempts = 4)
    {
        var rnd = new Random();

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("x-goog-api-key", apiKey);
            req.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

            try
            {
                var resp = await http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (resp.IsSuccessStatusCode)
                    return (resp, body);

                var code = (int)resp.StatusCode;
                var shouldRetry = code == 429 || (code >= 500 && code <= 599);
                if (!shouldRetry)
                {
                    resp.EnsureSuccessStatusCode(); // istisna fırlat
                }

                // Retry-After
                TimeSpan delay = TimeSpan.Zero;
                if (resp.Headers.TryGetValues("Retry-After", out var vals))
                {
                    var v = vals.FirstOrDefault();
                    if (int.TryParse(v, out var seconds))
                        delay = TimeSpan.FromSeconds(seconds);
                }
                if (delay == TimeSpan.Zero)
                {
                    var baseMs = (int)Math.Pow(2, attempt - 1) * 1000; // 1000, 2000, 4000
                    delay = TimeSpan.FromMilliseconds(baseMs + rnd.Next(0, 400));
                }

                if (attempt < maxAttempts)
                    await Task.Delay(delay, ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 + rnd.Next(0, 300)), ct);
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(700 + rnd.Next(0, 300)), ct);
            }
        }

        throw new HttpRequestException("Gemini servisine şu anda ulaşılamıyor (503/429). Lütfen biraz sonra tekrar deneyin.");
    }
}

// ===== JSON map =====
public class GeminiChecksResponse_Expanded
{
    [JsonPropertyName("checks")]
    public List<CheckResult> Checks { get; set; } = new();
}
