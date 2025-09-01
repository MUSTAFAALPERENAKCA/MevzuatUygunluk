using MevzuatUygunluk.Services;

var builder = WebApplication.CreateBuilder(args);

// MVC + Session
builder.Services.AddControllersWithViews();
builder.Services.AddSession(o =>
{
    o.IdleTimeout = TimeSpan.FromHours(2);
    o.Cookie.HttpOnly = true;
});

// HttpClients
builder.Services.AddHttpClient("Gemini", c =>
{
    // 503/timeout durumlarında uzun işlemler için makul süre
    c.Timeout = TimeSpan.FromMinutes(3);
});
builder.Services.AddHttpClient();

// Services
builder.Services.AddSingleton<IRequirementsStore, RequirementsStore>();
builder.Services.AddSingleton<IRegulationUploadCache, RegulationUploadCache>();
builder.Services.AddSingleton<IGeminiService, GeminiService>();
builder.Services.AddSingleton<IFeedbackStore, FeedbackStore>();
builder.Services.AddHostedService<StartupRequirementsHostedService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseSession();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Docs}/{action=Index}/{id?}");

app.Run();
