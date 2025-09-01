using MevzuatUygunluk.Services;

var builder = WebApplication.CreateBuilder(args);

// Services
builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();

// DI kayıtları
builder.Services.AddSingleton<IRequirementsStore, RequirementsStore>();
builder.Services.AddSingleton<IGeminiService, GeminiService>(); // <-- scoped yerine singleton
builder.Services.AddHostedService<StartupRequirementsHostedService>(); // başlangıçta şart üret

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
