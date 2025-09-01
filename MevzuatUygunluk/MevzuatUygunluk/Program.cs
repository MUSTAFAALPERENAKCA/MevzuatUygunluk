using MevzuatUygunluk.Services;

var builder = WebApplication.CreateBuilder(args);

// MVC
builder.Services.AddControllersWithViews();

// HTTP client: Gemini için uzun timeout
builder.Services.AddHttpClient("Gemini", c =>
{
    c.Timeout = TimeSpan.FromMinutes(10); // büyük PDF'ler ve uzun yanıtlar için
});
builder.Services.AddHttpClient(); // gerekirse başka yerlerde

// DI
builder.Services.AddSingleton<IRequirementsStore, RequirementsStore>();
builder.Services.AddSingleton<IGeminiService, GeminiService>();
builder.Services.AddHostedService<StartupRequirementsHostedService>(); // arka planda şart üretimi

var app = builder.Build();

// Pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
// app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Docs}/{action=Index}/{id?}");

app.Run();
