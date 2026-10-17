using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace MintVkBot.Services;

public class VkUserService
{
    private readonly HttpClient _httpClient;

    public VkUserService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    public async Task<string> GetRegistrationDateAsync(long userId)
    {
        try
        {
            var url = $"https://regvk.com/id{userId}";
            var response = await _httpClient.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(response);

            // RegVK обычно размещает информацию в тегах или тексте с датой
            var text = doc.DocumentNode.InnerText;
            var match = Regex.Match(text, @"(?:Дата регистрации|Зарегистрирован(?:а)?):\s*([0-9]{1,2}\s+[а-яА-ЯёЁ]+\s+[0-9]{4}(?:\s+года)?)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }

            // Попытка найти формат DD.MM.YYYY
            var matchDate = Regex.Match(text, @"\b([0-9]{2}\.[0-9]{2}\.[0-9]{4})\b");
            if (matchDate.Success)
            {
                return matchDate.Groups[1].Value.Trim();
            }

            return "Не удалось определить дату регистрации";
        }
        catch
        {
            return "Не удалось получить данные с сервиса";
        }
    }
}
