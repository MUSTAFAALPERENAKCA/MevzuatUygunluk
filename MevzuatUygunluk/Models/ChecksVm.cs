namespace MevzuatUygunluk.Models;

public class ChecksVm
{
    public List<CheckResult>? Checks { get; set; }
    public string? Error { get; set; }
}

public class CheckResult
{
    public string Requirement { get; set; } = "";
    public bool Present { get; set; }
    public string? Evidence { get; set; }
    public double? Confidence { get; set; }
    public List<int>? Pages { get; set; }
}

public class GeminiChecksResponse
{
    public List<CheckResult>? Checks { get; set; }
    public string? Overall_Summary { get; set; }
}
