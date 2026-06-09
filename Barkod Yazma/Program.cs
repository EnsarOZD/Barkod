using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddSingleton<DatabaseHelper>();

// Add Rate Limiter (Max 2 requests per 5 seconds per IP)
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("AntiSpam", limiterOptions =>
    {
        limiterOptions.PermitLimit = 2;
        limiterOptions.Window = TimeSpan.FromSeconds(5);
        limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        limiterOptions.QueueLimit = 0;
    });
});

builder.Services.AddCors();

var app = builder.Build();

app.UseRateLimiter();

// Allow serving index.html from wwwroot
app.UseStaticFiles();

// Load Configs
var pinFromConfig = app.Configuration["AppConfig:SecurityPin"] ?? "1453";
var printerName = app.Configuration["AppConfig:PrinterName"] ?? "ZDesigner LP 2844";
var maxBoxCount = int.Parse(app.Configuration["AppConfig:MaxBoxCount"] ?? "50");

// Middleware for PIN Check
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        if (context.Request.Path.Value == "/api/status") 
        {
            await next();
            return;
        }

        if (!context.Request.Headers.TryGetValue("X-Print-Pin", out var providedPin) || providedPin != pinFromConfig)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { success = false, message = "Geçersiz veya eksik PIN" });
            return;
        }
    }
    await next();
});

// GET Status
app.MapGet("/api/status", () => Results.Ok(new { status = "online", printer = printerName }));

// POST Print
app.MapPost("/api/print", async ([FromBody] PrintRequestDTO req, HttpContext ht, DatabaseHelper db) =>
{
    string clientIp = ht.Connection.RemoteIpAddress?.ToString() ?? "Unknown";

    // 1. Basic Validations
    if (string.IsNullOrWhiteSpace(req.IdempotencyKey))
        return Results.BadRequest(new { success = false, message = "Idempotency key eksik." });

    if (string.IsNullOrWhiteSpace(req.ProjectName) || string.IsNullOrWhiteSpace(req.Location))
        return Results.BadRequest(new { success = false, message = "Proje Adı veya Lokasyon boş olamaz." });
        
    if (req.ProjectCode <= 0)
        return Results.BadRequest(new { success = false, message = "Proje Kodu pozitif tam sayı olmalıdır." });

    if (req.BoxCount <= 0 || req.BoxCount > maxBoxCount)
        return Results.BadRequest(new { success = false, message = $"Koli Sayısı 1 ile {maxBoxCount} arasında olmalıdır." });

    // 2. Idempotency Check
    if (db.IsDuplicate(req.IdempotencyKey))
    {
        return Results.Conflict(new { success = false, message = "Bu etiket kısa süre önce işleme alınmış. Lütfen yeni bir liste oluşturun." });
    }

    try
    {
        // 3. Raw Print (EPL)
        PrintManager.PrintLabelEPL(printerName, req.ProjectName, req.Location, req.ProjectCode, req.BoxCount);

        // 4. Log to SQLite
        long logId = await db.LogPrintAsync(req.IdempotencyKey, req.ProjectName, req.Location, req.ProjectCode, req.BoxCount, "SUCCESS", null, clientIp);

        return Results.Ok(new { success = true, message = "Etiket yazdırma kuyruğuna başarıyla gönderildi.", historyId = logId });
    }
    catch (Exception ex)
    {
        await db.LogPrintAsync(req.IdempotencyKey, req.ProjectName, req.Location, req.ProjectCode, req.BoxCount, "ERROR", ex.Message, clientIp);
        return Results.Json(new { success = false, message = "Yazıcı hatası: " + ex.Message }, statusCode: 500);
    }
}).RequireRateLimiting("AntiSpam");

app.MapGet("/api/history", async (DatabaseHelper db) =>
{
    var logs = await db.GetRecentLogsAsync();
    return Results.Ok(logs);
});

// If no route matches, fallback to index.html (SPA feel)
app.MapFallbackToFile("index.html");

app.Run();

// DTO
public record PrintRequestDTO(string IdempotencyKey, string ProjectName, string Location, long ProjectCode, int BoxCount);
