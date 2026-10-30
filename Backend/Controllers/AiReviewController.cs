using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using SmartGreenhouse.Backend.Services;

namespace SmartGreenhouse.Backend.Controllers;

// Comment іде прямо в промпт до DeepSeek — довжина обмежена з тих самих
// причин, що й Notes у PlantingRequest.
public record AiReviewRequest([param: MaxLength(1000)] string? Comment);

// Позачерговий AI-огляд профілю з кнопки в застосунках, з необов'язковим
// коментарем від людини ("листя жовтіє знизу", "перенесла ближче до вікна").
// Огляд іде у фоні; новий профіль прийде через SignalR "PlantProfileReceived".
[ApiController]
[Route("api/ai-review")]
public class AiReviewController : ControllerBase
{
    private readonly AiAgronomistService _aiAgronomist;

    public AiReviewController(AiAgronomistService aiAgronomist)
    {
        _aiAgronomist = aiAgronomist;
    }

    [HttpPost]
    public IActionResult Post([FromBody] AiReviewRequest request)
    {
        if (!_aiAgronomist.TryStartManualReview(request.Comment))
        {
            return Conflict("AI review is already running");
        }

        // З тілом: ApiClient у застосунках парсить JSON у будь-якій 2xx, крім 204.
        return Accepted(new { status = "started" });
    }
}
