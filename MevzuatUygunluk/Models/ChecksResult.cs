using System.Text.Json.Serialization;

namespace MevzuatUygunluk.Models;

public class CheckResult
{
    [JsonPropertyName("requirement")]
    public string? Requirement { get; set; }

    [JsonPropertyName("present")]
    public bool Present { get; set; }

    [JsonPropertyName("evidence")]
    public string? Evidence { get; set; }

    [JsonPropertyName("pages")]
    public int[]? Pages { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("mustHave")]
    public bool MustHave { get; set; }

    // Çoklu dosya kanıtı
    [JsonPropertyName("evidence_by_file")]
    public List<FileEvidence> EvidenceByFile { get; set; } = new();

    // Mevzuat dayanağı
    [JsonPropertyName("law_refs")]
    public List<LawRef> LawRefs { get; set; } = new();
}

public class FileEvidence
{
    [JsonPropertyName("file")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("present")]
    public bool Present { get; set; }

    [JsonPropertyName("evidence")]
    public string? Evidence { get; set; }

    [JsonPropertyName("pages")]
    public int[]? Pages { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }
}

public class LawRef
{
    [JsonPropertyName("doc_name")]
    public string DocName { get; set; } = "";

    [JsonPropertyName("page")]
    public int? Page { get; set; }

    [JsonPropertyName("quote")]
    public string? Quote { get; set; }
}
