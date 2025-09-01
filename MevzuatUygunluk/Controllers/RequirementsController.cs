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
        var rel = _cfg["Regulations:LocalPath"] ?? "Data/mevzuat.pdf";
        var local = Path.Combine(_env.ContentRootPath, rel.Replace('/', Path.DirectorySeparatorChar));
        var count = int.TryParse(_cfg["Regulations:RequirementCount"], out var c) ? c : 20;

        var (fileUri, mime) = await _gemini.UploadLocalFileAsync(local);
        var generated = await _gemini.GenerateRequirementsFromRegulationAsync(fileUri, mime, count);

        await _store.SaveAsync(generated);

        TempData["msg"] = $"{generated.Requirements.Count} şart üretildi ve kaydedildi.";
        return RedirectToAction(nameof(Index));
    }
}
