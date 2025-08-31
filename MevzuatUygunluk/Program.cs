var builder = WebApplication.CreateBuilder(args);

// --- Services ---
builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();   // 🔑 IHttpClientFactory kaydı burada olacak

var app = builder.Build();

// --- Middleware pipeline ---
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// Eğer sadece HTTP kullanacaksan şunu kapatabilirsin
// app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseRouting();

// Default route’u Docs/Index yapıyoruz
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Docs}/{action=Index}/{id?}");

app.Run();
