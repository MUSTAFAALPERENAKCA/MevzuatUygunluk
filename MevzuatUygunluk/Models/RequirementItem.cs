namespace MevzuatUygunluk.Models;

public class RequirementItem
{
    // İnsan okunur şart cümlesi
    public string Requirement { get; set; } = "";

    // İlgili mevzuat madde/başlık referansı (varsa)
    public string? Article { get; set; }

    // Zorunlu mu? (Zorunlu(1) => true, Seçimli => false)
    public bool MustHave { get; set; } = true;

    // Kardinalite (0,1, n  -> n için MaxOccurs = -1 kullanılır)
    public int? MinOccurs { get; set; }
    public int? MaxOccurs { get; set; }

    // Alan adı ve bağlam (ör. PostalAddress.CityName)
    public string? Field { get; set; }
    public string? Section { get; set; }
}

public class GeneratedRequirements
{
    public List<RequirementItem> Requirements { get; set; } = new();
    public string? Notes { get; set; }
}
