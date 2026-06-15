# 📋 MevzuatUygunluk — Yapay Zeka Destekli Mevzuat Uygunluk Denetleyicisi

Yüklenen belgelerin ilgili mevzuata uygun olup olmadığını **Google Gemini AI** kullanarak otomatik olarak analiz eden ASP.NET Core web uygulaması.

## ✨ Özellikler

- **Belge Yükleme & Analiz** — PDF/metin belgelerini yükle, AI otomatik inceler
- **Mevzuat Kataloğu** — Tanımlı mevzuat gereksinimleri ve madde listeleri
- **Gemini AI Entegrasyonu** — Google Gemini ile derin metin anlama ve uygunluk değerlendirmesi
- **Gereksinim Takibi** — Hangi maddelerin karşılandığı / karşılanmadığı raporlama
- **Dinamik Kural Güncelleme** — Mevzuat gereksinimleri uygulama yeniden başlatılmadan güncellenebilir

## 🛠️ Tech Stack

| Katman | Teknoloji |
|--------|-----------|
| Framework | ASP.NET Core (.NET 8) |
| AI | Google Gemini API |
| ORM | Entity Framework Core |
| Mimari | MVC + Service Layer |
| Depolama | Dahili gereksinim deposu + DB |

## 📂 Proje Yapısı

```
MevzuatUygunluk/
├── Controllers/
│   ├── DocsController.cs           # Belge yükleme ve analiz
│   └── RequirementsController.cs   # Mevzuat yönetimi
├── Services/
│   ├── GeminiService.cs            # Gemini AI entegrasyonu
│   ├── RequirementsCatalog.cs      # Mevzuat kataloğu
│   ├── RequirementsStore.cs        # Veri erişimi
│   └── StartupRequirementsHostedService.cs  # Başlangıç yüklemesi
├── Models/                         # Domain modelleri
└── Views/                          # Kullanıcı arayüzü
```

## ⚡ Kurulum

**Gereksinimler:** .NET 8 SDK · Google Gemini API Key

```bash
git clone https://github.com/MUSTAFAALPERENAKCA/MevzuatUygunluk
cd MevzuatUygunluk

# appsettings.json içine Gemini API key ekle:
# "GeminiApiKey": "YOUR_API_KEY"

dotnet run
```

Gemini API key almak için: [Google AI Studio](https://aistudio.google.com/)

## 📄 Lisans

MIT
