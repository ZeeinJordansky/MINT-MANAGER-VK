using System.Diagnostics;
using VkNet.Abstractions;
using VkNet.Model;
using VkNet.Model.RequestParams;

namespace MintVkBot.Services;

public class LongPollService : BackgroundService
{
    private readonly IVkApi _vkApi;
    private readonly CommandHandler _commandHandler;
    private readonly ILogger<LongPollService> _logger;
    private readonly ulong _groupId = 232664735;

    public LongPollService(
        IVkApi vkApi,
        CommandHandler commandHandler,
        ILogger<LongPollService> logger)
    {
        _vkApi = vkApi;
        _commandHandler = commandHandler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Запуск Bots Long Poll сервиса для группы ID: {GroupId}", _groupId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Получаем сервер Long Poll для группы
                var serverResponse = await _vkApi.Groups.GetLongPollServerAsync(_groupId);
                var server = serverResponse.Server;
                var key = serverResponse.Key;
                var ts = serverResponse.Ts;

                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };

                _logger.LogInformation("Long Poll успешно подключен к {Server}", server);

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var url = $"{server}?act=a_check&key={key}&ts={ts}&wait=25";
                        var response = await httpClient.GetStringAsync(url, stoppingToken);

                        using var doc = System.Text.Json.JsonDocument.Parse(response);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("failed", out var failedProp))
                        {
                            var failed = failedProp.GetInt32();
                            _logger.LogWarning("LongPoll failed code: {Code}", failed);
                            if (failed == 1)
                            {
                                ts = root.GetProperty("ts").GetString();
                            }
                            else
                            {
                                break; // Получаем новый ключ и сервер
                            }
                            continue;
                        }

                        if (root.TryGetProperty("ts", out var newTsProp))
                        {
                            ts = newTsProp.GetString();
                        }

                        if (root.TryGetProperty("updates", out var updatesProp) && updatesProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var update in updatesProp.EnumerateArray())
                            {
                                if (update.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "message_new")
                                {
                                    if (update.TryGetProperty("object", out var objProp))
                                    {
                                        try
                                        {
                                            var sw = Stopwatch.StartNew();
                                            var json = objProp.GetRawText();
                                            var jToken = Newtonsoft.Json.Linq.JToken.Parse(json);
                                            var vkResponse = new VkNet.Utils.VkResponse(jToken);

                                            Message msg;
                                            if (vkResponse.ContainsKey("message"))
                                            {
                                                msg = Message.FromJson(vkResponse["message"]);
                                            }
                                            else
                                            {
                                                msg = Message.FromJson(vkResponse);
                                            }

                                            _ = Task.Run(async () =>
                                            {
                                                try
                                                {
                                                    await _commandHandler.ProcessMessageAsync(msg, sw);
                                                }
                                                catch (Exception ex)
                                                {
                                                    _logger.LogError(ex, "Ошибка обработки сообщения через Long Poll");
                                                }
                                            }, stoppingToken);
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogError(ex, "Ошибка парсинга события Long Poll");
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Ошибка Long Poll цикла: {Msg}", ex.Message);
                        await Task.Delay(2000, stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Ошибка подключения к Long Poll: {Msg}. Переподключение через 5 секунд...", ex.Message);
                await Task.Delay(5000, stoppingToken);
            }
        }
    }
}
