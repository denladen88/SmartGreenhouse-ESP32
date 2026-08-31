using Microsoft.Extensions.Options;
using SmartGreenhouse.Backend.Models;

namespace SmartGreenhouse.Backend.Middleware;

// Єдиний спільний ключ (заголовок X-Api-Key), достатній для застосунку, що
// живе тільки в локальній Wi-Fi мережі — див. розділ "Backend" плану
// мобільного застосунку. Застосовується до /api та /hubs, не до /capture
// (той лишається відкритим для сумісності з наявним ESP32/AI-циклом).
public class ApiKeyMiddleware
{
    private const string HeaderName = "X-Api-Key";

    private readonly RequestDelegate _next;

    public ApiKeyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IOptions<ApiOptions> apiOptions)
    {
        var path = context.Request.Path;
        var requiresKey = path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs");

        if (requiresKey)
        {
            var providedKey = context.Request.Headers[HeaderName].FirstOrDefault()
                ?? context.Request.Query["access_token"].FirstOrDefault(); // SignalR WebSocket handshake can't set headers

            if (string.IsNullOrEmpty(apiOptions.Value.Key) || providedKey != apiOptions.Value.Key)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Missing or invalid X-Api-Key");
                return;
            }
        }

        await _next(context);
    }
}
