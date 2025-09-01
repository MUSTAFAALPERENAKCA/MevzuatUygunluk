using MevzuatUygunluk.Services; // <-- gerekli

var builder = WebApplication.CreateBuilder(args);

// Services
builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();

// DI kayıtları (HATAYI ÇÖZER)
builder.Services.AddSingleton<IRequirementsStore, RequirementsStore>();
builder.Services.AddScoped<IGeminiService, GeminiService>();

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
