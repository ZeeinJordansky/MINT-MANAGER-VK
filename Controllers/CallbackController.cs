using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MintVkBot.Models;
using MintVkBot.Services;
using VkNet.Abstractions;
using VkNet.Model;
using VkNet.Utils;

namespace MintVkBot.Controllers;

[ApiController]
[Route("vk/api/ver1fLsv7Dfsdlmak")]
public class CallbackController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly CommandHandler _commandHandler;
    private readonly ILogger<CallbackController> _logger;

    public CallbackController(
        IConfiguration config,
        CommandHandler commandHandler,
        ILogger<CallbackController> logger)
    {
        _config = config;
        _commandHandler = commandHandler;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> HandleCallback([FromBody] VkCallbackDto request)
    {
        var sw = Stopwatch.StartNew();

        // 1. Проверка Secret Key от VK Callback API для защиты эндпоинта
        var expectedSecret = _config["VK_SECRET_KEY"] ?? _config["VkConfig:SecretKey"];
        if (!string.IsNullOrEmpty(expectedSecret) && request.Secret != expectedSecret)
        {
            _logger.LogWarning("Отклонен запрос с неверным секретным ключом.");
            return Forbid();
        }

        // 2. Обработка подтверждения адреса сервера (confirmation)
        if (request.Type == "confirmation")
        {
            var confirmationCode = _config["VK_CONFIRMATION_CODE"] ?? _config["VkConfig:ConfirmationCode"];
            _logger.LogInformation("Возврат кода подтверждения VK Callback API");
            return Content(confirmationCode ?? "", "text/plain");
        }

        // 3. Обработка входящего сообщения
        if (request.Type == "message_new" && request.Object.HasValue)
        {
            try
            {
                var json = request.Object.Value.GetRawText();
                var vkResponse = new VkResponse(Newtonsoft.Json.Linq.JToken.Parse(json));
                
                // В зависимости от версии API, сообщение может быть внутри поля "message" или прямо в корне объекта
                Message message;
                if (vkResponse.ContainsKey("message"))
                {
                    message = Message.FromJson(vkResponse["message"]);
                }
                else
                {
                    message = Message.FromJson(vkResponse);
                }

                // Фоновая обработка команды без задержки ответа серверу VK
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _commandHandler.ProcessMessageAsync(message, sw);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка обработки сообщения ID: {Id}", message.Id);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при парсинге входящего сообщения");
            }

            // VK API требует вернуть строку "ok"
            return Content("ok", "text/plain");
        }

        return Content("ok", "text/plain");
    }

    [HttpGet("health")]
    public IActionResult HealthCheck()
    {
        return Ok(new { status = "healthy", service = "Mint Vk Manager", timestamp = DateTime.UtcNow });
    }
}
