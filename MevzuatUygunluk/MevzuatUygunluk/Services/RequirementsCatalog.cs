namespace MevzuatUygunluk.Services;

public static class RequirementsCatalog
{
    public static readonly IReadOnlyList<string> Default = new List<string>
    {
        "Tedarikçi adı ve unvanı belirtilmiş olmalı",
        "Belgede tarih ve yetkili imza bulunmalı",
        "Toplam tutar KDV dahil açıkça yazılmalı",
        "Belgede belge numarası yer almalı",
        "İletişim bilgileri (adres veya telefon) bulunmalı"
    };
}
