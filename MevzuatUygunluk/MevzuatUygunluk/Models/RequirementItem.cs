namespace MevzuatUygunluk.Models;

public class RequirementItem
{
    public string Requirement { get; set; } = "";
    public string? Article { get; set; }
    public bool MustHave { get; set; } = true;
}

public class GeneratedRequirements
{
    public List<RequirementItem> Requirements { get; set; } = new();
    public string? Notes { get; set; }
}
