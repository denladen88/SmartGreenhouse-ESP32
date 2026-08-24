using DotNetEnv;
using Microsoft.EntityFrameworkCore;
using SmartGreenhouse.Backend.Data;
using SmartGreenhouse.Backend.Models;
using SmartGreenhouse.Backend.Services;

if (File.Exists(".env"))
{
    Env.Load();
}

// ContentRootPath явно фіксується на папку самого exe (а не поточну робочу
// директорію), бо WORKDIR у контейнері навмисно вказує на окрему теку для
// даних (greenhouse.db/Photos) — інакше appsettings.json мовчки не знайдеться.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.Configure<GeminiOptions>(builder.Configuration.GetSection("Gemini"));
builder.Services.Configure<Esp32Options>(builder.Configuration.GetSection("Esp32"));
builder.Services.Configure<AiAgronomistOptions>(builder.Configuration.GetSection("AiAgronomist"));
builder.Services.Configure<PlantOptions>(builder.Configuration.GetSection("Plant"));

builder.Services.AddDbContext<AppDbContext>();

builder.Services.AddHttpClient(nameof(AiAgronomistService), client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddSingleton<MqttBackgroundService>();
builder.Services.AddSingleton<IMqttPublisher>(sp => sp.GetRequiredService<MqttBackgroundService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttBackgroundService>());
builder.Services.AddHostedService<AiAgronomistService>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

Directory.CreateDirectory(AiAgronomistService.PhotosDirectory);

host.Run();
