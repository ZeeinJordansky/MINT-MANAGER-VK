using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MintVkBot.Data;
using MintVkBot.Models;
using VkNet.Abstractions;
using VkNet.Model;
using VkNet.Model.RequestParams;

namespace MintVkBot.Services;

public class CommandHandler
{
    private readonly IVkApi _vkApi;
    private readonly IServiceProvider _serviceProvider;
    private readonly VkUserService _vkUserService;
    private readonly ILogger<CommandHandler> _logger;

    public CommandHandler(
        IVkApi vkApi,
        IServiceProvider serviceProvider,
        VkUserService vkUserService,
        ILogger<CommandHandler> logger)
    {
        _vkApi = vkApi;
        _serviceProvider = serviceProvider;
        _vkUserService = vkUserService;
        _logger = logger;
    }

    public async Task ProcessMessageAsync(Message message, Stopwatch? sw = null)
    {
        var peerId = message.PeerId ?? 0;
        var fromId = message.FromId ?? 0;

        if (peerId == 0 || fromId <= 0) return; // Игнорируем сообщения от групп или некорректные

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // Обновляем статистику сообщений
        var member = await db.ChatMembers.FirstOrDefaultAsync(m => m.PeerId == peerId && m.UserId == fromId);
        var today = DateTime.UtcNow.Date;

        if (member == null)
        {
            member = new ChatMember
            {
                PeerId = peerId,
                UserId = fromId,
                Role = ChatRole.User,
                NickName = null,
                MessagesToday = 1,
                TotalMessages = 1,
                LastMessageDate = DateTime.UtcNow,
                LastActivityDate = today
            };
            db.ChatMembers.Add(member);
        }
        else
        {
            if (member.LastActivityDate == null || member.LastActivityDate.Value.Date != today)
            {
                member.MessagesToday = 1;
                member.LastActivityDate = today;
            }
            else
            {
                member.MessagesToday++;
            }
            member.TotalMessages++;
            member.LastMessageDate = DateTime.UtcNow;
        }

        // Проверка мута: если пользователь в активном муте, удаляем сообщение
        if (member.MutedUntil.HasValue)
        {
            if (member.MutedUntil.Value > DateTime.UtcNow)
            {
                // Пользователь в муте - удаляем сообщение для всех
                try
                {
                    if (message.ConversationMessageId.HasValue)
                    {
                        await _vkApi.Messages.DeleteAsync(
                            new[] { (ulong)message.Id!.Value },
                            deleteForAll: true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Не удалось удалить сообщение пользователя в муте: {Msg}", ex.Message);
                }
                await db.SaveChangesAsync();
                return;
            }
            else
            {
                // Мут истек
                member.MutedUntil = null;
                member.MuteReason = null;
                member.MutedByUserId = null;
            }
        }

        await db.SaveChangesAsync();

        var text = message.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return;

        // Нормализация команды (например /ping, /stats, /q, /help, /id, /minfo, /mute, /unmute)
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();

        // Убираем упоминание бота (например /ping@botname -> /ping)
        if (command.Contains('@'))
        {
            command = command.Split('@')[0];
        }

        switch (command)
        {
            case "/ping":
                await HandlePingAsync(message, sw);
                break;
            case "/stats":
                await HandleStatsAsync(message, member, db);
                break;
            case "/q":
                await HandleQuitAsync(message, member, db);
                break;
            case "/help":
                await HandleHelpAsync(message);
                break;
            case "/id":
                await HandleIdAsync(message);
                break;
            case "/minfo":
                await HandleMinfoAsync(message, member, db);
                break;
            case "/mute":
                await HandleMuteAsync(message, member, parts, db);
                break;
            case "/unmute":
                await HandleUnmuteAsync(message, member, parts, db);
                break;
        }
    }

    private async Task HandlePingAsync(Message message, Stopwatch? sw)
    {
        var pingSw = Stopwatch.StartNew();
        // Измеряем время вызова VK API
        try
        {
            _ = await _vkApi.Utils.GetServerTimeAsync();
        }
        catch { }
        pingSw.Stop();

        sw?.Stop();
        var responseTime = sw != null ? $"{sw.ElapsedMilliseconds} ms" : "0 ms";

        var response = $"🏓 Понг! Пинг: {pingSw.ElapsedMilliseconds} ms | Скорость ответа: {responseTime}";
        await SendReplyAsync(message, response);
    }

    private async Task HandleStatsAsync(Message message, ChatMember member, BotDbContext db)
    {
        var targetUserId = GetTargetUserId(message) ?? message.FromId!.Value;
        var targetMember = targetUserId == member.UserId
            ? member
            : await db.ChatMembers.FirstOrDefaultAsync(m => m.PeerId == message.PeerId!.Value && m.UserId == targetUserId);

        var userInfo = await GetUserMentionAsync(targetUserId);
        var settings = await db.ChatSettings.FirstOrDefaultAsync(s => s.PeerId == message.PeerId!.Value);
        var warnLimit = settings?.WarnLimit ?? 3;

        var nick = targetMember?.NickName ?? "Не установлен";
        var role = targetMember?.Role.GetRoleName() ?? ChatRole.User.GetRoleName();
        var warns = targetMember?.WarningsCount ?? 0;
        var isMuted = (targetMember?.MutedUntil.HasValue == true && targetMember.MutedUntil > DateTime.UtcNow) ? "Да" : "Нет";

        string assignedByText;
        if (targetMember == null || targetMember.Role == ChatRole.User || targetMember.AssignedByUserId == null)
        {
            assignedByText = "[https://vk.ru/club239281784|[«𝗠𝗶𝗻𝘁» – чат-менеджер](https://vk.ru/cm_mint)]";
        }
        else
        {
            assignedByText = await GetUserMentionAsync(targetMember.AssignedByUserId.Value);
        }

        var assignedDateText = targetMember?.AssignedAt?.ToString("dd.MM.yyyy HH:mm") ?? "—";
        var messagesToday = targetMember?.MessagesToday ?? 0;
        var totalMessages = targetMember?.TotalMessages ?? 0;
        var lastMsgDateText = targetMember?.LastMessageDate?.ToString("dd.MM.yyyy HH:mm:ss") ?? "—";

        var response =
$@"📊 Информация о пользователе {userInfo}
📁 Общая информация:
├ Nick_Name: {nick}
├ VK ID: {targetUserId}
└ Должность в беседе: {role}
🛡️ Модераторские данные:
├ Предупреждения: {warns}/{warnLimit}
├ Активный мут в беседе: {isMuted}
├ Должность выдал: {assignedByText}
└ Дата назначения: {assignedDateText}
💬 Статистика активности:
├ Сообщений за сегодня: {messagesToday}
├ Всего сообщений: {totalMessages}
└ Последнее сообщение: {lastMsgDateText}";

        await SendReplyAsync(message, response);
    }

    private async Task HandleQuitAsync(Message message, ChatMember member, BotDbContext db)
    {
        var peerId = message.PeerId!.Value;
        var userId = message.FromId!.Value;
        var userInfo = await GetUserMentionAsync(userId);

        try
        {
            // В VK API бесед peerId = 2000000000 + chatId
            long chatId = peerId > 2000000000 ? peerId - 2000000000 : peerId;

            // Исключаем участника
            await _vkApi.Messages.RemoveChatUserAsync((ulong)chatId, userId);

            bool hadRoleOrNick = (member.Role != ChatRole.User) || !string.IsNullOrEmpty(member.NickName);

            // Сбрасываем должность и ник
            member.Role = ChatRole.User;
            member.NickName = null;
            member.AssignedByUserId = null;
            member.AssignedAt = null;
            await db.SaveChangesAsync();

            var response = $"{userInfo} был(а) исключён(а) из беседы по собственному желанию.";
            if (hadRoleOrNick)
            {
                response += "\n💬 Также должность и Nick у пользователя были сняты.";
            }

            await SendReplyAsync(message, response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка при кике пользователя {UserId}", userId);
            await SendReplyAsync(message, "К сожалению вас не удалось исключить из беседы, возможно вы имеете права системного администратора (звёздочку).");
        }
    }

    private async Task HandleHelpAsync(Message message)
    {
        var text =
@"📋 Список доступных команд чат-менеджера «Mint»:

🔹 Основные команды (для всех пользователей):
/ping — Проверить пинг и скорость отклика бота
/stats — Просмотр своей статистики или упомянутого пользователя
/id — Информация о профиле и дате регистрации ВКонтакте
/q — Выйти из беседы по собственному желанию
/help — Справка по командам бота

🛡️ Команды модерации:
/minfo — Информация о своей должности и правах
/mute [ссылка/ответ] [время, напр. 10m, 1h, 1d] [причина] — Заблокировать отправку сообщений
/unmute [ссылка/ответ] — Снять блокировку чата

💡 Бот требует права администратора в беседе для модерации и удаления нарушений.";

        await SendReplyAsync(message, text);
    }

    private async Task HandleIdAsync(Message message)
    {
        var targetUserId = GetTargetUserId(message) ?? message.FromId!.Value;
        var userInfo = await GetUserMentionAsync(targetUserId);
        var regDate = await _vkUserService.GetRegistrationDateAsync(targetUserId);

        var response =
$@"🔢 Информация о профиле {userInfo}
🆔 VK ID: {targetUserId}
📅 Дата регистрации: {regDate}";

        await SendReplyAsync(message, response);
    }

    private async Task HandleMinfoAsync(Message message, ChatMember member, BotDbContext db)
    {
        var targetUserId = GetTargetUserId(message) ?? message.FromId!.Value;
        var targetMember = targetUserId == member.UserId
            ? member
            : await db.ChatMembers.FirstOrDefaultAsync(m => m.PeerId == message.PeerId!.Value && m.UserId == targetUserId);

        var currentRole = targetMember?.Role ?? ChatRole.User;
        if (currentRole == ChatRole.User && targetUserId == member.UserId)
        {
            await SendReplyAsync(message, "❌ Данная команда доступна только модераторам и администраторам беседы.");
            return;
        }

        var userInfo = await GetUserMentionAsync(targetUserId);
        var assignedByText = (targetMember?.AssignedByUserId.HasValue == true)
            ? await GetUserMentionAsync(targetMember.AssignedByUserId.Value)
            : "[https://vk.ru/club239281784|[«𝗠𝗶𝗻𝘁» – чат-менеджер](https://vk.ru/cm_mint)]";

        var assignedDateText = targetMember?.AssignedAt?.ToString("dd.MM.yyyy HH:mm") ?? "—";

        var response =
$@"👤 Информация о должности пользователя {userInfo}
💼 Должность: {currentRole.GetRoleName()}
🔰 Уровень прав: {currentRole.GetPermissionLevel()}
📅 Дата назначения: {assignedDateText}
👮 Назначил(-а): {assignedByText}";

        await SendReplyAsync(message, response);
    }

    private async Task HandleMuteAsync(Message message, ChatMember member, string[] parts, BotDbContext db)
    {
        // Проверка прав: Модератор и выше
        if (member.Role < ChatRole.Moderator)
        {
            await SendReplyAsync(message, "❌ Команда доступна с должности Модератор и выше.");
            return;
        }

        var targetUserId = GetTargetUserId(message);
        if (targetUserId == null)
        {
            await SendReplyAsync(message, "❌ Укажите пользователя (ответом на сообщение или ссылкой): /mute [пользователь] [время: 10m/2h/1d] [причина]");
            return;
        }

        // Нельзя мутить самого себя или тех, у кого роль выше или равна
        var targetMember = await db.ChatMembers.FirstOrDefaultAsync(m => m.PeerId == message.PeerId!.Value && m.UserId == targetUserId.Value);
        var targetRole = targetMember?.Role ?? ChatRole.User;

        if (targetRole >= member.Role && member.Role != ChatRole.ChatOwner)
        {
            await SendReplyAsync(message, "❌ Вы не можете замутить пользователя с равной или более высокой должностью.");
            return;
        }

        // Парсинг времени и причины
        // Аргументы могут идти после упоминания или сразу, если ответом
        var args = parts.Skip(1).ToList();
        // Если первое слово - упоминание или ссылка, пропускаем его
        if (args.Count > 0 && (args[0].StartsWith("[id") || args[0].StartsWith("@") || args[0].Contains("vk.com") || args[0].Contains("vk.ru")))
        {
            args.RemoveAt(0);
        }

        TimeSpan duration = TimeSpan.FromMinutes(30); // По умолчанию
        string durationStr = "30m";
        string reason = "Нарушение правил беседы";

        if (args.Count > 0)
        {
            var parsedTime = ParseDuration(args[0]);
            if (parsedTime.HasValue)
            {
                duration = parsedTime.Value;
                durationStr = args[0];
                args.RemoveAt(0);
            }
        }

        if (args.Count > 0)
        {
            reason = string.Join(" ", args);
        }

        if (targetMember == null)
        {
            targetMember = new ChatMember
            {
                PeerId = message.PeerId!.Value,
                UserId = targetUserId.Value,
                Role = ChatRole.User
            };
            db.ChatMembers.Add(targetMember);
        }

        var until = DateTime.UtcNow.Add(duration);
        targetMember.MutedUntil = until;
        targetMember.MuteReason = reason;
        targetMember.MutedByUserId = member.UserId;
        await db.SaveChangesAsync();

        var targetMention = await GetUserMentionAsync(targetUserId.Value);
        var adminMention = await GetUserMentionAsync(member.UserId);

        var response =
$@"🔇 {targetMention} получил(-а) блокировку чата на {durationStr}.
📝 Причина: {reason}
⏳ Срок действия до: {until.ToLocalTime():dd.MM.yyyy HH:mm:ss}
👮 Выдал(а): {adminMention}";

        await SendReplyAsync(message, response);
    }

    private async Task HandleUnmuteAsync(Message message, ChatMember member, string[] parts, BotDbContext db)
    {
        if (member.Role < ChatRole.Moderator)
        {
            await SendReplyAsync(message, "❌ Команда доступна с должности Модератор и выше.");
            return;
        }

        var targetUserId = GetTargetUserId(message);
        if (targetUserId == null)
        {
            await SendReplyAsync(message, "❌ Укажите пользователя (ответом на сообщение или ссылкой): /unmute [пользователь]");
            return;
        }

        var targetMember = await db.ChatMembers.FirstOrDefaultAsync(m => m.PeerId == message.PeerId!.Value && m.UserId == targetUserId.Value);
        if (targetMember == null || targetMember.MutedUntil == null || targetMember.MutedUntil <= DateTime.UtcNow)
        {
            await SendReplyAsync(message, "❌ У данного пользователя нет активной блокировки чата.");
            return;
        }

        targetMember.MutedUntil = null;
        targetMember.MuteReason = null;
        targetMember.MutedByUserId = null;
        await db.SaveChangesAsync();

        var targetMention = await GetUserMentionAsync(targetUserId.Value);
        var adminMention = await GetUserMentionAsync(member.UserId);

        var response =
$@"🔊 {targetMention} больше не имеет блокировки чата.
👮 Снял(-а): {adminMention}";

        await SendReplyAsync(message, response);
    }

    private async Task SendReplyAsync(Message message, string text)
    {
        try
        {
            await _vkApi.Messages.SendAsync(new MessagesSendParams
            {
                PeerId = message.PeerId,
                RandomId = new Random().NextInt64(),
                Message = text,
                ReplyTo = message.Id
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при отправке сообщения в peerId: {PeerId}", message.PeerId);
        }
    }

    private async Task<string> GetUserMentionAsync(long userId)
    {
        try
        {
            var users = await _vkApi.Users.GetAsync(new[] { userId });
            var u = users.FirstOrDefault();
            if (u != null)
            {
                return $"[id{userId}|{u.FirstName} {u.LastName}]";
            }
        }
        catch { }

        return $"[id{userId}|ID: {userId}]";
    }

    private long? GetTargetUserId(Message message)
    {
        // 1. Из Reply-сообщения
        if (message.ReplyMessage?.FromId != null && message.ReplyMessage.FromId > 0)
        {
            return message.ReplyMessage.FromId.Value;
        }

        // 2. Из пересланных сообщений
        if (message.ForwardedMessages?.Count > 0 && message.ForwardedMessages[0].FromId > 0)
        {
            return message.ForwardedMessages[0].FromId;
        }

        // 3. Из текста через регулярку [id12345|...] или @id12345 или https://vk.com/id12345
        var text = message.Text ?? string.Empty;
        var match = Regex.Match(text, @"\[id(\d+)\|[^\]]+\]|@id(\d+)|\bvk\.com\/id(\d+)\b");
        if (match.Success)
        {
            for (int i = 1; i <= 3; i++)
            {
                if (match.Groups[i].Success && long.TryParse(match.Groups[i].Value, out var id))
                {
                    return id;
                }
            }
        }

        return null;
    }

    private TimeSpan? ParseDuration(string input)
    {
        var match = Regex.Match(input.ToLowerInvariant(), @"^(\d+)([smhd])$");
        if (!match.Success) return null;

        var val = int.Parse(match.Groups[1].Value);
        return match.Groups[2].Value switch
        {
            "s" => TimeSpan.FromSeconds(val),
            "m" => TimeSpan.FromMinutes(val),
            "h" => TimeSpan.FromHours(val),
            "d" => TimeSpan.FromDays(val),
            _ => null
        };
    }
}
