using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SmartGreenhouse.Backend.Models;

namespace SmartGreenhouse.Backend.Controllers;

// Проксі до ESP32-CAM /capture — застосунок звертається лише до Backend, не
// потребує окремого маршруту до самого ESP32 в мережі.
[ApiController]
[Route("api/camera")]
public class CameraController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Esp32Options _esp32Options;

    public CameraController(IHttpClientFactory httpClientFactory, IOptions<Esp32Options> esp32Options)
    {
        _httpClientFactory = httpClientFactory;
        _esp32Options = esp32Options.Value;
    }

    [HttpGet("snapshot")]
    public async Task<IActionResult> GetSnapshot(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("Esp32Camera");

        byte[] imageBytes;
        try
        {
            imageBytes = await client.GetByteArrayAsync(_esp32Options.CameraUrl, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, "Failed to reach ESP32 camera");
        }

        // ESP32 повертає 204 без тіла вночі (замало Lux для корисного кадру) — те
        // саме, на що зважає AiAgronomistService.RunProfileAnalysisAsync.
        return imageBytes.Length == 0 ? NoContent() : File(imageBytes, "image/jpeg");
    }
}
