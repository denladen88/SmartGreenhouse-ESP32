using DotNetEnv;
using Microsoft.EntityFrameworkCore;
using SmartGreenhouse.Backend.Data;
using SmartGreenhouse.Backend.Hubs;
using SmartGreenhouse.Backend.Middleware;
using SmartGreenhouse.Backend.Models;
using SmartGreenhouse.Backend.Services;

if (File.Exists(".env"))
{
    Env.Load();
}

// ContentRootPath явно фіксується на папку самого exe (а не поточну робочу
// директорію), бо WORKDIR у контейнері навмисно вказує на окрему теку для
// даних (greenhouse.db/Photos) — інакше appsettings.json мовчки не знайдеться.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Залишаємо тільки Console + Debug. За замовчуванням на Windows додається ще
// EventLog-провайдер, який при зупинці диспоузиться раніше за фонові сервіси —
// їхня спроба залогувати останню помилку кидає ObjectDisposed
// ('EventLogInternal'), і цей виняток загортає СПРАВЖНЮ помилку в
// AggregateException "An error occurred while writing to logger(s)", роблячи
// причину краху нечитабельною.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.Configure<GeminiOptions>(builder.Configuration.GetSection("Gemini"));
builder.Services.Configure<Esp32Options>(builder.Configuration.GetSection("Esp32"));
builder.Services.Configure<AiAgronomistOptions>(builder.Configuration.GetSection("AiAgronomist"));
builder.Services.Configure<PlantOptions>(builder.Configuration.GetSection("Plant"));
builder.Services.Configure<ApiOptions>(builder.Configuration.GetSection("Api"));
builder.Services.Configure<DataRetentionOptions>(builder.Configuration.GetSection("DataRetention"));

builder.Services.AddDbContext<AppDbContext>();

builder.Services.AddHttpClient(nameof(AiAgronomistService), client =>
{
    // Shared by the fast ESP32 /capture GET and the slower Gemini vision POST (image + prompt) —
    // 30s was cutting it close for Gemini now that photos are larger (UXGA capture).
    client.Timeout = TimeSpan.FromSeconds(45);
});

// Окремий, коротший таймаут для CameraController: це живий проксі-запит з
// телефону, а не фоновий AI-цикл — застосунок не повинен зависати на 45с,
// чекаючи на мертвий ESP32.
builder.Services.AddHttpClient("Esp32Camera", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});

// Сигнал "прийшла нова телеметрія": MqttBackgroundService штовхає, локальний
// контролер AiAgronomistService прокидається одразу, а не чекає свій таймер.
builder.Services.AddSingleton<TelemetrySignal>();

builder.Services.AddSingleton<MqttBackgroundService>();
builder.Services.AddSingleton<IMqttPublisher>(sp => sp.GetRequiredService<MqttBackgroundService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttBackgroundService>());

// Реєстрований як singleton (а не лише AddHostedService<T>), щоб
// PlantingController міг інжектити той самий екземпляр і викликати
// TriggerImmediateProfileAnalysisAsync — той самий патерн, що й вище для
// MqttBackgroundService/IMqttPublisher.
builder.Services.AddSingleton<AiAgronomistService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AiAgronomistService>());

builder.Services.AddHostedService<DataRetentionService>();

builder.Services.AddControllers();
builder.Services.AddSignalR();

// AllowAnyOrigin є прийнятним тут: авторизація йде через заголовок
// X-Api-Key (і ?access_token= для SignalR), а не cookie, тож
// AllowCredentials не потрібен і з AllowAnyOrigin не конфліктує. У проді
// WebApp роздається цим самим Backend (той самий origin) — CORS там узагалі
// не задіюється; потрібен лише для розробки WebApp окремим Vite dev-сервером
// (localhost:5173) проти Backend на іншому порту.
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

Directory.CreateDirectory(AiAgronomistService.PhotosDirectory);

app.UseCors();
// Роздає зібраний WebApp з wwwroot/ (не в git — це build-артефакт, див.
// WebApp/README) і віддає index.html на будь-який невідомий шлях, щоб
// клієнтський роутинг (react-router-dom) сам розібрався з адресою після
// прямого переходу/оновлення сторінки в браузері.
app.UseStaticFiles();
app.UseMiddleware<ApiKeyMiddleware>();
app.MapControllers();
app.MapHub<TelemetryHub>("/hubs/live");
app.MapFallbackToFile("index.html");

app.Run();
