namespace MevzuatUygunluk.Models;

public class FeedbackItem
{
    public string RequirementKey { get; set; } = "";  // normalize edilmiş requirement
    public string Scenario { get; set; } = "";
    public string InvoiceType { get; set; } = "";
    public bool? PresentOverride { get; set; }        // kullanıcı düzeltmesi
    public string? EvidenceOverride { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
