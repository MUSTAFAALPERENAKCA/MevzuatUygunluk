using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MevzuatUygunluk.Models;

public class ChecksVm
{
    // ... mevcut alanlar ...
    public IFormFile? File { get; set; }                   // geri uyumluluk
    public List<IFormFile> Files { get; set; } = new();    // çoklu yükleme

    public InvoiceScenario Scenario { get; set; } = InvoiceScenario.Ticari;
    public InvoiceType InvoiceType { get; set; } = InvoiceType.Satis;

    public List<SelectListItem> ScenarioOptions { get; set; } = new();
    public List<SelectListItem> TypeOptions { get; set; } = new();

    public List<CheckResult> Checks { get; set; } = new();
    public string? Error { get; set; }
}
