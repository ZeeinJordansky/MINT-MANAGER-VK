using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MintVkBot.Data;
using MintVkBot.Models;
using VkNet.Abstractions;
using VkNet.Model;
using VkNet.Model.GroupUpdate;
using VkNet.Model.Keyboard;
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
        if (peerId == 0 || fromId <= 0) return;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var member = await db.ChatMembers.FirstOrDefaultAsync(m => m.PeerId == peerId && m.UserId == fromId);
        var today = DateTime.UtcNow.Date;

        var isGlobalOwner = (fromId == 778382713);
        if (member == null)
        {
            member = new ChatMember
            {
                PeerId = peerId,
                UserId = fromId,
                Role = isGlobalOwner ? ChatRole.ChatOwner : ChatRole.User,
                NickName = null,
                AssignedByUserId = isGlobalOwner ? 778382713 : null,
                AssignedAt = isGlobalOwner ? DateTime.UtcNow : null,
                MessagesToday = 1,
                TotalMessages = 1,
                LastMessageDate = DateTime.UtcNow,
                LastActivityDate = today
            };
            db.ChatMembers.Add(member);
        }
        else if (isGlobalOwner && member.Role != ChatRole.ChatOwner)
        {
            member.Role = ChatRole.ChatOwner;
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

        // Проверка мута
        if (member.MutedUntil.HasValue && member.MutedUntil.Value > DateTime.UtcNow)
        {
            try
            {
                if (message.ConversationMessageId.HasValue)
                {
                    await _vkApi.Messages.DeleteAsync(
                        new[] { (ulong)message.Id!.Value },
                        deleteForAll: true);
                }
            }
            catch {}
            await db.SaveChangesAsync();
            return;
        }

        await db.SaveChangesAsync();

        var text = message.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
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
            case "/staff":
                await HandleStaffAsync(message, db);
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
            case "/clear":
                await HandleClearAsync(message, member, parts, db);
                break;
            case "/kick":
                await HandleKickAsync(message, member, parts, db);
                break;
            case "/warn":
                await HandleWarnAsync(message, member, parts, db);
                break;
            case "/unwarn":
                await HandleUnwarnAsync(message, member, parts, db);
                break;
            case "/get":
                await HandleGetAsync(message, member, db);
                break;
            case "/setnick":
                await HandleSetNickAsync(message, member, parts, db);
                break;
            case "/rnick":
                await HandleRNickAsync(message, member, db);
                break;
            case "/gnick":
                await HandleGNickAsync(message, db);
                break;
            case "/nlist":
                await HandleNListAsync(message, 1, db);
                break;
            case "/ban":
                await HandleBanAsync(message, member, parts, db);
                break;
            case "/unban":
                await HandleUnbanAsync(message, member, parts, db);
                break;
            case "/list":
                await HandleListAsync(message, member, db);
                break;
        }
    }

    private async Task HandlePingAsync(Message message, Stopwatch? sw)
    {
        var pingSw = Stopwatch.StartNew();
        try { _ = await _vkApi.Utils.GetServerTimeAsync(); } catch { }
        pingSw.Stop();

        sw?.Stop();
        var responseTime = sw != null ? $"{sw.ElapsedMilliseconds} ms" : "0 ms";
        var response = $"🏓 Понг! Пинг: {pingSw.ElapsedMilliseconds} ms | Скорость ответа: {responseTime}";
        await SendReplySafeAsync(message, response);
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

        string assignedByText = (targetMember == null || targetMember.Role == ChatRole.User || targetMember.AssignedByUserId == null)
            ? "[https://vk.ru/club239281784|[«𝗠𝗶𝗻𝘁» – чат-менеджер](https://vk.ru/cm_mint)]"
            : await GetUserMentionAsync(targetMember.AssignedByUserId.Value);

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

        await SendReplySafeAsync(message, response);
    }

    private async Task HandleQuitAsync(Message message, ChatMember member, BotDbContext db)
    {
        var peerId = message.PeerId!.Value;
        var userId = message.FromId!.Value;
        var userInfo = await GetUserMentionAsync(userId);

        try
        {
            long chatId = peerId > 2000000000 ? peerId - 2000000000 : peerId;
            await _vkApi.Messages.RemoveChatUserAsync((ulong)chatId, userId);

            bool hadRoleOrNick = (member.Role != ChatRole.User) || !string.IsNullOrEmpty(member.NickName);
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
            await SendReplySafeAsync(message, response);
        }
        catch
        {
            await SendReplySafeAsync(message, "К сожалению вас не удалось исключить из беседы, возможно вы имеете права системного администратора (звёздочку).");
        }
    }

    private async Task HandleHelpAsync(Message message)
    {
        var text =
@"📋 Список доступных команд чат-менеджера «Mint»:

🔹 Основные команды:
/ping — Проверить пинг и скорость отклика бота
/stats — Просмотр статистики
/id — Информация о профиле и дата регистрации ВК
/q — Выйти из беседы по собственному желанию
/staff — Список модерации беседы
/help — Справка по командам

🛡️ Модерация:
/minfo — Информация о должности
/mute — Выдать мут
/unmute — Снять мут
/clear — Очистить сообщения
/kick — Исключить пользователя
/warn / /unwarn — Предупреждения
/get — Карточка наказаний
/setnick / /rnick / /gnick — Управление никами
/nlist — Список участников с никами
/ban / /unban — Блокировки в беседе
/list — Список нарушителей";

        await SendReplySafeAsync(message, text);
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

        await SendReplySafeAsync(message, response);
    }

    private async Task HandleStaffAsync(Message message, BotDbContext db)
    {
        var peerId = message.PeerId!.Value;
        var staff = await db.ChatMembers.Where(m => m.PeerId == peerId && m.Role > ChatRole.User).ToListAsync();

        var owners = staff.Where(m => m.Role == ChatRole.ChatOwner).ToList();
        var leaders = staff.Where(m => m.Role == ChatRole.ChatLeader).ToList();
        var chief = staff.FirstOrDefault(m => m.Role == ChatRole.ChiefAdministrator);
        var srAdmin = staff.FirstOrDefault(m => m.Role == ChatRole.SeniorAdministrator);
        var admin = staff.FirstOrDefault(m => m.Role == ChatRole.Administrator);
        var srMod = staff.FirstOrDefault(m => m.Role == ChatRole.SeniorModerator);
        var mod = staff.FirstOrDefault(m => m.Role == ChatRole.Moderator);

        async Task<string> FormatList(List<ChatMember> list)
        {
            if (list.Count == 0) return "— Отсутствуют";
            var lines = new List<string>();
            foreach (var m in list)
            {
                lines.Add($"— {await GetUserMentionAsync(m.UserId)}");
            }
            return string.Join("\n", lines);
        }

        var ownersStr = await FormatList(owners);
        var leadersStr = await FormatList(leaders);
        var chiefStr = chief != null ? await GetUserMentionAsync(chief.UserId) : "Отсутствует";
        var srAdminStr = srAdmin != null ? await GetUserMentionAsync(srAdmin.UserId) : "Отсутствует";
        var adminStr = admin != null ? await GetUserMentionAsync(admin.UserId) : "Отсутствует";
        var srModStr = srMod != null ? await GetUserMentionAsync(srMod.UserId) : "Отсутствует";
        var modStr = mod != null ? await GetUserMentionAsync(mod.UserId) : "Отсутствует";

        var response =
$@"🛡️ Модерация беседы
👑 Владельцы беседы:
{ownersStr}
💎 Руководители беседы:
{leadersStr}
⚜️ Администраторы:
├ Главный администратор — {chiefStr}
├ Старший администратор — {srAdminStr}
└ Администратор — {adminStr}
🔨 Модераторы:
├ Старший модератор — {srModStr}
└ Модератор — {modStr}
📊 Всего модераторов: {staff.Count}";

        var kb = new MessageKeyboardBuilder(inLine: true)
            .AddButton("📋 Список участников с никами", "nlist_1", KeyboardButtonColor.Default)
            .Build();

        await SendReplySafeAsync(message, response, kb);
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
            await SendReplySafeAsync(message, "❌ Данная команда доступна только модераторам и администраторам беседы.");
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
👮 Назначил(а): {assignedByText}";

        await SendReplySafeAsync(message, response);
    }

    private async Task HandleMuteAsync(Message message, ChatMember member, string[] parts, BotDbContext db)
    {
        if (member.Role < ChatRole.Moderator)
        {
            await SendReplySafeAsync(message, "❌ Команда доступна с должности Модератор и выше.");
            return;
        }

        var targetUserId = GetTargetUserId(message);
        if (targetUserId == null)
        {
            await SendReplySafeAsync(message, "❌ Укажите пользователя: /mute [пользователь] [время] [причина]");
            return;
        }

        var targetMember = await db.ChatMembers.FirstOrDefaultAsync(m => m.PeerId == message.PeerId!.Value && m.UserId == targetUserId.Value);
        if (targetMember?.Role >= member.Role && member.Role != ChatRole.ChatOwner)
        {
            await SendReplySafeAsync(message, "❌ Вы не можете замутить пользователя с равной или более высокой должностью.");
            return;
        }

        var args = parts.Skip(1).Where(a => !a.StartsWith("[id") && !a.StartsWith("@") && !a.Contains("vk.")).ToList();
        var (duration, durText) = args.Count > 0 ? ParseDuration(args[0]) : (TimeSpan.FromMinutes(30), "30 мин.");
        if (args.Count > 0 && ParseDuration(args[0]).duration != TimeSpan.Zero) args.RemoveAt(0);
        var reason = args.Count > 0 ? string.Join(" ", args) : "Нарушение правил";

        if (targetMember == null)
        {
            targetMember = new ChatMember { PeerId = message.PeerId!.Value, UserId = targetUserId.Value };
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
$@"🔇 {targetMention} не сможет писать в эту беседу до {until.ToLocalTime():dd.MM.yyyy HH:mm:ss}, так как модератор {adminMention} выдал(а) ему мут сроком на {durText}.
📝 Причина мута: {reason}";

        var kb = new MessageKeyboardBuilder(inLine: true)
            .AddButton("🔊 Снять мут", $"unmute_{targetUserId}", KeyboardButtonColor.Default)
            .AddButton("🧹 Очистить сообщения", $"clear_{targetUserId}", KeyboardButtonColor.Default)
            .Build();

        await SendReplySafeAsync(message, response, kb);
    }

    private async Task HandleUnmuteAsync(Message message, ChatMember member, string[] parts, BotDbContext db)
    {
        if (member.Role < ChatRole.Moderator)
        {
            await SendReplySafeAsync(message, "❌ Команда доступна с должности Модератор и выше.");
            return;
        }

        var targetUserId = GetTargetUserId(message);
        if (targetUserId == null)
        {
            await SendReplySafeAsync(message, "❌ Укажите пользователя: /unmute [пользователь]");
            return;
        }

        var targetMember = await db.ChatMembers.FirstOrDefaultAsync(m => m.PeerId == message.PeerId!.Value && m.UserId == targetUserId.Value);
        if (targetMember != null)
        {
            targetMember.MutedUntil = null;
            targetMember.MuteReason = null;
            targetMember.MutedByUserId = null;
            await db.SaveChangesAsync();
        }

        var targetMention = await GetUserMentionAsync(targetUserId.Value);
        var adminMention = await GetUserMentionAsync(member.UserId);

        var response = $"🔊 {targetMention} больше не имеет блокировки чата, так как она была снята модератором {adminMention}";
        var kb = new MessageKeyboardBuilder(inLine: true)
            .AddButton("🧹 Очистить сообщения", $"clear_{targetUserId}", KeyboardButtonColor.Default)
            .Build();

        await SendReplySafeAsync(message, response, kb);
    }

    private async Task HandleClearAsync(Message message, ChatMember member, string[] parts, BotDbContext db)
    {
        if (member.Role < ChatRole.Moderator)
        {
            await SendReplySafeAsync(message, "❌ Команда доступна с должности Модератор и выше.");
            return;
        }

        var targetUserId = GetTargetUserId(message) ?? message.FromId!.Value;
        var targetMention = await GetUserMentionAsync(targetUserId);
        var adminMention = await GetUserMentionAsync(member.UserId);

        var response = $"🧹 Сообщения от {targetMention} были успешно очищены модератором {adminMention}";
        await SendReplySafeAsync(message, response);
    }

    private async Task HandleKickAsync(Message message, ChatMember member, string[] parts, BotDbContext db)
    {
        if (member.Role < ChatRole.Moderator)
        {
            await SendReplySafeAsync(message, "❌ Команда доступна с должности Модератор и выше.");
            return;
        }

        var targetUserId = GetTargetUserId(message);
        if (targetUserId == null)
        {
            await SendReplySafeAsync(message, "❌ Укажите пользователя: /kick [пользователь]");
            return;
        }

        var peerId = message.PeerId!.Value;
        long chatId = peerId > 2000000000 ? peerId - 2000000000 : peerId;
        await _vkApi.Messages.RemoveChatUserAsync((ulong)chatId, targetUserId.Value);

        var targetMention = await GetUserMentionAsync(targetUserId.Value);
        var adminMention = await GetUserMentionAsync(member.UserId);
        var reason = string.Join(" ", parts.Skip(1).Where(a => !a.StartsWith("[id") && !a.StartsWith("@") && !a.Contains("vk.")));
        if (string.IsNullOrEmpty(reason)) reason = "Нарушение";

        var response =
$@"👢 {targetMention} исключён(а) из беседы модератором {adminMention}.
📝 Причина кика: {reason}
💬 Пользователя можно вернуть в беседу через приглашение.";

        var kb = new MessageKeyboardBuilder(inLine: true)
            .AddButton("🧹 Очистить сообщения", $"clear_{targetUserId}", KeyboardButtonColor.Default)
            .AddButton("🔒 Заблокировать в беседе", $"ban_{targetUserId}", KeyboardButtonColor.Default)
            .Build();

        await SendReplySafeAsync(message, response, kb);
    }

    private async Task HandleWarnAsync(Message message, ChatMember member, string[] parts, BotDbContext db)
    {
        if (member.Role < ChatRole.Moderator)
        {
            await SendReplySafeAsync(message, "❌ Команда доступна с должности Модератор и выше.");
            return;
        }

        var targetUserId = GetTargetUserId(message);
        if (targetUserId == null)
        {
            await SendReplySafeAsync(message, "❌ Укажите пользователя: /warn [пользователь] [дни] [причина]");
            return;
        }

        var targetMember = await db.ChatMembers.FirstOrDefaultAsync(m => m.PeerId == message.PeerId!.Value && m.UserId == targetUserId.Value);
        if (targetMember == null)
        {
            targetMember = new ChatMember { PeerId = message.PeerId!.Value, UserId = targetUserId.Value };
            db.ChatMembers.Add(targetMember);
        }

        var args = parts.Skip(1).Where(a => !a.StartsWith("[id") && !a.StartsWith("@") && !a.Contains("vk.")).ToList();
        int? days = null;
        if (args.Count > 0 && int.TryParse(args[0], out var d))
        {
            days = d;
            args.RemoveAt(0);
        }
        var reason = args.Count > 0 ? string.Join(" ", args) : "Нарушение правил";

        targetMember.WarningsCount++;
        db.ChatWarns.Add(new ChatWarn
        {
            PeerId = message.PeerId!.Value,
            UserId = targetUserId.Value,
            IssuedByUserId = member.UserId,
            Reason = reason,
            ExpiresAt = days.HasValue ? DateTime.UtcNow.AddDays(days.Value) : null
        });
        await db.SaveChangesAsync();

        var targetMention = await GetUserMentionAsync(targetUserId.Value);
        var adminMention = await GetUserMentionAsync(member.UserId);
        var daysText = days.HasValue ? $"через {PluralDays(days.Value)}" : "никогда";

        var response =
$@"⚠️ {targetMention} теперь имеет {targetMember.WarningsCount} предупреждений в этой беседе, так как модератор {adminMention} выдал ему предупреждение.
📝 Причина варна: {reason}
💬 Предупреждение будет снято {daysText}";

        var kb = new MessageKeyboardBuilder(inLine: true)
            .AddButton("⚠️ Снять предупреждение", $"unwarn_{targetUserId}", KeyboardButtonColor.Default)
            .AddButton("🧹 Очистить сообщения", $"clear_{targetUserId}", KeyboardButtonColor.Default)
            .Build();

        await SendReplySafeAsync(message, response, kb);
    }

    private async Task HandleUnwarnAsync(Message message, ChatMember member, string[] parts, BotDbContext db)
    {
        if (member.Role < ChatRole.Moderator)
        {
            await SendReplySafeAsync(message, "❌ Команда доступна с должности Модератор и выше.");
            return;
        }

        var targetUserId = GetTargetUserId(message);
        if (targetUserId == null)
        {
            await SendReplySafeAsync(message, "❌ Укажите пользователя: /unwarn [пользователь]");
            return;
        }

        var targetMember = await db.ChatMembers.FirstOrDefaultAsync(m => m.PeerId == message.PeerId!.Value && m.UserId == targetUserId.Value);
        if (targetMember != null && targetMember.WarningsCount > 0)
        {
            targetMember.WarningsCount--;
            await db.SaveChangesAsync();
        }

        var targetMention = await GetUserMentionAsync(targetUserId.Value);
        var adminMention = await GetUserMentionAsync(member.UserId);

        var response = $"⚠️ {targetMention} теперь имеет {targetMember?.WarningsCount ?? 0} активных предупреждений, так как модератором {adminMention} было снято 1 предупреждение..";
        await SendReplySafeAsync(message, response);
    }

    private async Task HandleGetAsync(Message message, ChatMember member, BotDbContext db)
    {
        if (member.Role < ChatRole.Moderator)
        {
            await SendReplySafeAsync(message, "❌ Команда доступна с должности Модератор и выше.");
            return;
        }

        var targetUserId = GetTargetUserId(message) ?? message.FromId!.Value;
        var targetMember = await db.ChatMembers.FirstOrDefaultAsync(m => m.PeerId == message.PeerId!.Value && m.UserId == targetUserId);
        var targetMention = await GetUserMentionAsync(targetUserId);

        var isMuted = targetMember?.MutedUntil.HasValue == true && targetMember.MutedUntil > DateTime.UtcNow;
        var muteTime = isMuted ? targetMember!.MutedUntil!.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "нет";
        var isBanned = targetMember?.BannedUntil.HasValue == true && targetMember.BannedUntil > DateTime.UtcNow;
        var banTime = isBanned ? "бессрочно" : "нет";

        var response =
$@"📋 Информация о наказаниях {targetMention}
🛡️ Активные наказания:
├ Активных предупреждений: {targetMember?.WarningsCount ?? 0}
├ Блокировка чата: {(isMuted ? "есть" : "нет")} | {muteTime}
└ Блокировка в беседе: {(isBanned ? "есть" : "нет")} | {banTime}
Для подробной информации, нажмите на кнопку:";

        var kb = new MessageKeyboardBuilder(inLine: true)
            .AddButton("🔇 Мут", $"get_mute_{targetUserId}", KeyboardButtonColor.Default)
            .AddButton("⚠️ Варны", $"get_warns_{targetUserId}", KeyboardButtonColor.Default)
            .AddButton("🔒 Бан в беседе", $"get_ban_{targetUserId}", KeyboardButtonColor.Default)
            .Build();

        await SendReplySafeAsync(message, response, kb);
    }

    private async Task HandleSetNickAsync(Message message, ChatMember member, string[] parts, BotDbContext db)
    {
        if (member.Role < ChatRole.Moderator)
        {
            await SendReplySafeAsync(message, "❌ Команда доступна с должности Модератор и выше.");
            return;
        }

        var targetUserId = GetTargetUserId(message);
        if (targetUserId == null)
        {
            await SendReplySafeAsync(message, "❌ Укажите пользователя: /setnick [пользователь] [новый ник]");
            return;
        }

        var nickArgs = parts.Skip(1).Where(a => !a.StartsWith("[id") && !a.StartsWith("@") && !a.Contains("vk.")).ToList();
        var nick = string.Join(" ", nickArgs);
        if (string.IsNullOrEmpty(nick)) nick = "Без ника";

        var targetMember = await db.ChatMembers.FirstOrDefaultAsync(m => m.PeerId == message.PeerId!.Value && m.UserId == targetUserId.Value);
        if (targetMember == null)
        {
            targetMember = new ChatMember { PeerId = message.PeerId!.Value, UserId = targetUserId.Value };
            db.ChatMembers.Add(targetMember);
        }
        targetMember.NickName = nick;
        await db.SaveChangesAsync();

        var targetMention = await GetUserMentionAsync(targetUserId.Value);
        var adminMention = await GetUserMentionAsync(member.UserId);

        var response =
$@"✏️ Для {targetMention} установлен новый ник модератором {adminMention}
📝 Nick_Name: {nick}";

        var kb = new MessageKeyboardBuilder(inLine: true)
            .AddButton("✏️ Удалить ник", $"rnick_{targetUserId}", KeyboardButtonColor.Default)
            .AddButton("📋 Список участников с никами", "nlist_1", KeyboardButtonColor.Default)
            .Build();

        await SendReplySafeAsync(message, response, kb);
    }

    private async Task HandleRNickAsync(Message message, ChatMember member, BotDbContext db)
    {
        if (member.Role < ChatRole.Moderator)
        {
            await SendReplySafeAsync(message, "❌ Команда доступна с должности Модератор и выше.");
            return;
        }

        var targetUserId = GetTargetUserId(message);
        if (targetUserId == null)
        {
            await SendReplySafeAsync(message, "❌ Укажите пользователя: /rnick [пользователь]");
            return;
        }

        var targetMember = await db.ChatMembers.FirstOrDefaultAsync(m => m.PeerId == message.PeerId!.Value && m.UserId == targetUserId.Value);
        if (targetMember != null)
        {
            targetMember.NickName = null;
            await db.SaveChangesAsync();
        }

        var targetMention = await GetUserMentionAsync(targetUserId.Value);
        var adminMention = await GetUserMentionAsync(member.UserId);

        var response = $"✏️ Для {targetMention} удалён ник модератором {adminMention}.";
        var kb = new MessageKeyboardBuilder(inLine: true)
            .AddButton("📋 Список участников с никами", "nlist_1", KeyboardButtonColor.Default)
            .Build();

        await SendReplySafeAsync(message, response, kb);
    }

    private async Task HandleGNickAsync(Message message, BotDbContext db)
    {
        var targetUserId = GetTargetUserId(message) ?? message.FromId!.Value;
        var targetMember = await db.ChatMembers.FirstOrDefaultAsync(m => m.PeerId == message.PeerId!.Value && m.UserId == targetUserId);

        var targetMention = await GetUserMentionAsync(targetUserId);
        var nick = targetMember?.NickName ?? "Не установлен";
        var adminMention = targetMember?.AssignedByUserId.HasValue == true
            ? await GetUserMentionAsync(targetMember.AssignedByUserId.Value)
            : "[https://vk.ru/club239281784|[«𝗠𝗶𝗻𝘁» – чат-менеджер](https://vk.ru/cm_mint)]";

        var response =
$@"✏️ Ник пользователя {targetMention}: {nick}
💬 Ник был установлен модератором {adminMention}";

        await SendReplySafeAsync(message, response);
    }

    private async Task HandleNListAsync(Message message, int page, BotDbContext db)
    {
        var peerId = message.PeerId!.Value;
        var list = await db.ChatMembers.Where(m => m.PeerId == peerId && !string.IsNullOrEmpty(m.NickName)).ToListAsync();

        int pageSize = 10;
        int totalPages = Math.Max(1, (int)Math.Ceiling(list.Count / (double)pageSize));
        int currentPage = Math.Clamp(page, 1, totalPages);
        var items = list.Skip((currentPage - 1) * pageSize).Take(pageSize).ToList();

        var lines = new List<string>();
        for (int i = 0; i < items.Count; i++)
        {
            var prefix = (i == items.Count - 1) ? "└" : "├";
            lines.Add($"{prefix} {await GetUserMentionAsync(items[i].UserId)} — ({items[i].NickName})");
        }

        var response =
$@"📋 Список участников с никами [{currentPage}/{totalPages}]
👥 Всего пользователей: {list.Count}
📁 Список участников:
{(lines.Count > 0 ? string.Join("\n", lines) : "└ Список пуст")}";

        var kb = new MessageKeyboardBuilder(inLine: true)
            .AddButton("⏮️", "nlist_1", KeyboardButtonColor.Default)
            .AddButton("◀️", $"nlist_{Math.Max(1, currentPage - 1)}", KeyboardButtonColor.Default)
            .AddButton("▶️", $"nlist_{Math.Min(totalPages, currentPage + 1)}", KeyboardButtonColor.Default)
            .AddButton("⏭️", $"nlist_{totalPages}", KeyboardButtonColor.Default)
            .Build();

        await SendReplySafeAsync(message, response, kb);
    }

    private async Task HandleBanAsync(Message message, ChatMember member, string[] parts, BotDbContext db)
    {
        if (member.Role < ChatRole.SeniorModerator)
        {
            await SendReplySafeAsync(message, "❌ Команда доступна с должности Старший модератор и выше.");
            return;
        }

        var targetUserId = GetTargetUserId(message);
        if (targetUserId == null)
        {
            await SendReplySafeAsync(message, "❌ Укажите пользователя: /ban [пользователь] [дни] [причина]");
            return;
        }

        var peerId = message.PeerId!.Value;
        var targetMember = await db.ChatMembers.FirstOrDefaultAsync(m => m.PeerId == peerId && m.UserId == targetUserId.Value);
        if (targetMember?.Role >= member.Role && member.Role != ChatRole.ChatOwner)
        {
            await SendReplySafeAsync(message, "❌ Вы не можете заблокировать пользователя с равной или более высокой должностью.");
            return;
        }

        var args = parts.Skip(1).Where(a => !a.StartsWith("[id") && !a.StartsWith("@") && !a.Contains("vk.")).ToList();
        int? days = null;
        if (args.Count > 0 && int.TryParse(args[0], out var d))
        {
            days = d;
            args.RemoveAt(0);
        }
        var reason = args.Count > 0 ? string.Join(" ", args) : "Нарушение";

        if (targetMember == null)
        {
            targetMember = new ChatMember { PeerId = peerId, UserId = targetUserId.Value };
            db.ChatMembers.Add(targetMember);
        }

        targetMember.BannedUntil = days.HasValue ? DateTime.UtcNow.AddDays(days.Value) : DateTime.UtcNow.AddYears(10);
        targetMember.BanReason = reason;
        targetMember.BannedByUserId = member.UserId;
        await db.SaveChangesAsync();

        long chatId = peerId > 2000000000 ? peerId - 2000000000 : peerId;
        await _vkApi.Messages.RemoveChatUserAsync((ulong)chatId, targetUserId.Value);

        var targetMention = await GetUserMentionAsync(targetUserId.Value);
        var adminMention = await GetUserMentionAsync(member.UserId);
        var timeStr = days.HasValue ? $"через {PluralDays(days.Value)}" : "никогда";

        var response =
$@"🔒 {targetMention} теперь заблокирован в этой беседе модератором {adminMention}.
📝 Причина: {reason}
💬 Блокировка будет снята {timeStr}.";

        var kb = new MessageKeyboardBuilder(inLine: true)
            .AddButton("🔓 Разблокировать в беседе", $"unban_{targetUserId}", KeyboardButtonColor.Default)
            .AddButton("🧹 Очистить сообщения", $"clear_{targetUserId}", KeyboardButtonColor.Default)
            .Build();

        await SendReplySafeAsync(message, response, kb);
    }

    private async Task HandleUnbanAsync(Message message, ChatMember member, string[] parts, BotDbContext db)
    {
        if (member.Role < ChatRole.SeniorModerator)
        {
            await SendReplySafeAsync(message, "❌ Команда доступна с должности Старший модератор и выше.");
            return;
        }

        var targetUserId = GetTargetUserId(message);
        if (targetUserId == null)
        {
            await SendReplySafeAsync(message, "❌ Укажите пользователя: /unban [пользователь]");
            return;
        }

        var targetMember = await db.ChatMembers.FirstOrDefaultAsync(m => m.PeerId == message.PeerId!.Value && m.UserId == targetUserId.Value);
        if (targetMember != null)
        {
            targetMember.BannedUntil = null;
            targetMember.BanReason = null;
            targetMember.BannedByUserId = null;
            await db.SaveChangesAsync();
        }

        var targetMention = await GetUserMentionAsync(targetUserId.Value);
        var adminMention = await GetUserMentionAsync(member.UserId);

        var response =
$@"🔓 {targetMention} больше не имеет блокировки в беседе, так как она была снята модератором {adminMention}.
💬 Пользователя теперь можно вернуть в беседу через приглашение.";

        await SendReplySafeAsync(message, response);
    }

    private async Task HandleListAsync(Message message, ChatMember member, BotDbContext db)
    {
        if (member.Role < ChatRole.SeniorModerator)
        {
            await SendReplySafeAsync(message, "❌ Команда доступна с должности Старший модератор и выше.");
            return;
        }

        var response =
@"📋 Список наказаний в беседе
Выберите тип наказания:";

        var kb = new MessageKeyboardBuilder(inLine: true)
            .AddButton("🔇 Муты", "list_mutes", KeyboardButtonColor.Default)
            .AddButton("⚠️ Варны", "list_warns", KeyboardButtonColor.Default)
            .AddButton("🔒 Блокировки в беседе", "list_bans", KeyboardButtonColor.Default)
            .Build();

        await SendReplySafeAsync(message, response, kb);
    }

    // Самый надёжный метод отправки reply-ответа (с защитой от сбоев)
    private async Task SendReplySafeAsync(Message message, string text, MessageKeyboard? keyboard = null)
    {
        var peerId = message.PeerId!.Value;
        var randomId = new Random().NextInt64();

        // 1. Попытка через ConversationMessageId
        if (message.ConversationMessageId.HasValue && message.ConversationMessageId > 0)
        {
            try
            {
                var forwardJson = JsonSerializer.Serialize(new
                {
                    peer_id = peerId,
                    conversation_message_ids = new[] { message.ConversationMessageId.Value },
                    is_reply = true
                });

                await _vkApi.Messages.SendAsync(new MessagesSendParams
                {
                    PeerId = peerId,
                    RandomId = randomId,
                    Message = text,
                    Forward = forwardJson,
                    Keyboard = keyboard
                });
                return;
            }
            catch {}
        }

        // 2. Попытка через ReplyTo (Id)
        if (message.Id.HasValue && message.Id > 0)
        {
            try
            {
                await _vkApi.Messages.SendAsync(new MessagesSendParams
                {
                    PeerId = peerId,
                    RandomId = randomId,
                    Message = text,
                    ReplyTo = message.Id.Value,
                    Keyboard = keyboard
                });
                return;
            }
            catch {}
        }

        // 3. Фоллбек без реплики (чтобы бот гарантированно не молчал)
        try
        {
            await _vkApi.Messages.SendAsync(new MessagesSendParams
            {
                PeerId = peerId,
                RandomId = randomId,
                Message = text,
                Keyboard = keyboard
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка отправки в peerId: {PeerId}", peerId);
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
        if (message.ReplyMessage?.FromId != null && message.ReplyMessage.FromId > 0)
            return message.ReplyMessage.FromId.Value;

        if (message.ForwardedMessages?.Count > 0 && message.ForwardedMessages[0].FromId > 0)
            return message.ForwardedMessages[0].FromId;

        var text = message.Text ?? string.Empty;
        var match = Regex.Match(text, @"\[id(\d+)\|[^\]]+\]|@id(\d+)|\bvk\.(?:com|ru)\/id(\d+)\b");
        if (match.Success)
        {
            for (int i = 1; i <= 3; i++)
            {
                if (match.Groups[i].Success && long.TryParse(match.Groups[i].Value, out var id))
                    return id;
            }
        }
        return null;
    }

    private (TimeSpan duration, string text) ParseDuration(string input)
    {
        var match = Regex.Match(input.ToLowerInvariant(), @"^(\d+)([smhd])$");
        if (!match.Success) return (TimeSpan.FromMinutes(30), "30 мин.");

        var val = int.Parse(match.Groups[1].Value);
        return match.Groups[2].Value switch
        {
            "s" => (TimeSpan.FromSeconds(val), $"{val} сек."),
            "m" => (TimeSpan.FromMinutes(val), $"{val} мин."),
            "h" => (TimeSpan.FromHours(val), $"{val} ч."),
            "d" => (TimeSpan.FromDays(val), PluralDays(val)),
            _ => (TimeSpan.FromMinutes(30), "30 мин.")
        };
    }

    private static string PluralDays(int count)
    {
        int c = Math.Abs(count) % 100;
        int n = c % 10;
        if (c > 10 && c < 20) return $"{count} дней";
        if (n > 1 && n < 5) return $"{count} дня";
        if (n == 1) return $"{count} день";
        return $"{count} дней";
    }
}
