using Microsoft.AspNetCore.Mvc;
using MevzuatUygunluk.Models;
using MevzuatUygunluk.Services;

namespace MevzuatUygunluk.Controllers;

public class RequirementsController : Controller
{
    private readonly IGeminiService _gemini;
    private readonly IRequirementsStore _store;
    private readonly IConfiguration _cfg;
    private readonly IWebHostEnvironment _env;

    public RequirementsController(IGeminiService gemini, IRequirementsStore store, IConfiguration cfg, IWebHostEnvironment env)
    {
        _gemini = gemini;
        _store = store;
        _cfg = cfg;
        _env = env;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var data = await _store.LoadAsync() ?? new GeneratedRequirements();
        ViewBag.StorePath = _store.StorePath;
        ViewBag.Count = data.Requirements.Count;
        return View(data);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Regenerate()
    {
        var rels = _cfg.GetSection("Regulations:Sources").Get<string[]>() ?? Array.Empty<string>();
        if (rels.Length == 0)
        {
            TempData["msg"] = "Regulations:Sources boş.";
            return RedirectToAction(nameof(Index));
        }

        var uploaded = new List<(string fileUri, string mimeType)>();
        foreach (var rel in rels)
        {
            var local = Path.Combine(_env.ContentRootPath, rel.Replace('/', Path.DirectorySeparatorChar));
            uploaded.Add(await _gemini.UploadLocalFileAsync(local));
        }

        var count = int.TryParse(_cfg["Regulations:RequirementCount"], out var c) ? c : 30;
        var generated = await _gemini.GenerateRequirementsFromSourcesAsync(uploaded, count);

        await _store.SaveAsync(generated);

        TempData["msg"] = $"{generated.Requirements.Count} şart üretildi ve kaydedildi.";
        return RedirectToAction(nameof(Index));
    }
}
