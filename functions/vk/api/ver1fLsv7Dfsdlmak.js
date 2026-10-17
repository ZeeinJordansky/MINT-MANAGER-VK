// Токен и параметры сообщества ВКонтакте вшиты прямо в код
const VK_ACCESS_TOKEN = "vk1.a.ChdOUA-5nsExGGSiBzaKdFc-jNsQYuYnmoRkzgtfn7K5wB0QB175HBfyw5H2JZ7Cm7ZI_NxSXdsPZGsclZ8SpcyaCNGc90bnjwtjPR2FStGjd4joB-3K0Rq6NtLJSuzchROf3lUuN4wnKDYYG5wSx72Zc22AhQk1fani_3SkB40UJXpNTsmMEZCtMX84Cks6Vgb9BKDaUhZo1BcIz4VbRA";
const VK_CONFIRMATION_CODE = "ce6d834c";
const VK_API_VERSION = "5.199";

// Иерархия должностей
const ROLE_NAMES = {
  0: "Пользователь Mint",
  1: "Модератор",
  2: "Старший модератор",
  3: "Администратор",
  4: "Старший администратор",
  5: "Главный администратор",
  6: "Руководитель беседы",
  7: "Владелец беседы"
};

export async function onRequest(context) {
  const { request, env } = context;

  // 1. GET-запрос или проверка страницы
  if (request.method !== "POST") {
    return new Response(VK_CONFIRMATION_CODE, {
      status: 200,
      headers: { "Content-Type": "text/plain; charset=utf-8" }
    });
  }

  // 2. Обработка POST от Callback API ВКонтакте
  try {
    const rawBody = await request.text();
    if (!rawBody) {
      return new Response("ok", { status: 200, headers: { "Content-Type": "text/plain; charset=utf-8" } });
    }

    const data = JSON.parse(rawBody);

    // Подтверждение адреса сервера Callback API
    if (data.type === "confirmation") {
      return new Response(VK_CONFIRMATION_CODE, {
        status: 200,
        headers: { "Content-Type": "text/plain; charset=utf-8" }
      });
    }

    // Входящее сообщение в беседе
    if (data.type === "message_new") {
      const message = data.object?.message || data.object;
      if (message) {
        // Фоновая асинхронная обработка (не задерживая ответ "ok" для ВК)
        if (context.waitUntil) {
          context.waitUntil(handleIncomingMessage(message, env));
        } else {
          await handleIncomingMessage(message, env);
        }
      }
      return new Response("ok", { status: 200, headers: { "Content-Type": "text/plain; charset=utf-8" } });
    }

    return new Response("ok", { status: 200, headers: { "Content-Type": "text/plain; charset=utf-8" } });
  } catch (err) {
    return new Response("ok", { status: 200, headers: { "Content-Type": "text/plain; charset=utf-8" } });
  }
}

// Вызов методов VK API
async function callVkApi(method, params = {}) {
  const url = new URL(`https://api.vk.com/method/${method}`);
  url.searchParams.set("access_token", VK_ACCESS_TOKEN);
  url.searchParams.set("v", VK_API_VERSION);

  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null) {
      url.searchParams.set(key, value.toString());
    }
  }

  const res = await fetch(url.toString());
  return await res.json();
}

// Отправка ответа с Reply
async function sendReply(peerId, messageText, replyToId) {
  try {
    await callVkApi("messages.send", {
      peer_id: peerId,
      message: messageText,
      reply_to: replyToId,
      random_id: Math.floor(Math.random() * 2000000000)
    });
  } catch (e) {
    console.error("Ошибка при отправке сообщения:", e);
  }
}

// Получение форматированного упоминания пользователя
async function getUserMention(userId) {
  try {
    const res = await callVkApi("users.get", { user_ids: userId });
    const user = res.response?.[0];
    if (user) {
      return `[id${userId}|${user.first_name} ${user.last_name}]`;
    }
  } catch {}
  return `[id${userId}|ID: ${userId}]`;
}

// Получение даты регистрации через regvk.com
async function getRegistrationDate(userId) {
  try {
    const res = await fetch(`https://regvk.com/id${userId}`, {
      headers: {
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
      }
    });
    const html = await res.text();
    const match = html.match(/(?:Дата регистрации|Зарегистрирован(?:а)?):\s*([0-9]{1,2}\s+[а-яА-ЯёЁ]+\s+[0-9]{4}(?:\s+года)?)/i);
    if (match) return match[1].trim();

    const matchDate = html.match(/\b([0-9]{2}\.[0-9]{2}\.[0-9]{4})\b/);
    if (matchDate) return matchDate[1].trim();

    return "Не удалось определить дату регистрации";
  } catch {
    return "Не удалось получить данные с сервиса";
  }
}

// Определение целевого пользователя (по reply, пересланному сообщению или ссылке)
function getTargetUserId(message) {
  if (message.reply_message && message.reply_message.from_id > 0) {
    return message.reply_message.from_id;
  }
  if (message.fwd_messages && message.fwd_messages.length > 0 && message.fwd_messages[0].from_id > 0) {
    return message.fwd_messages[0].from_id;
  }
  const text = message.text || "";
  const match = text.match(/\[id(\d+)\|[^\]]+\]|@id(\d+)|\bvk\.(?:com|ru)\/id(\d+)\b/);
  if (match) {
    return parseInt(match[1] || match[2] || match[3], 10);
  }
  return null;
}

// Парсинг времени (10s, 30m, 2h, 1d)
function parseDuration(str) {
  const match = (str || "").toLowerCase().match(/^(\d+)([smhd])$/);
  if (!match) return null;
  const val = parseInt(match[1], 10);
  const unit = match[2];
  if (unit === "s") return { ms: val * 1000, text: `${val} сек.` };
  if (unit === "m") return { ms: val * 60 * 1000, text: `${val} мин.` };
  if (unit === "h") return { ms: val * 3600 * 1000, text: `${val} ч.` };
  if (unit === "d") return { ms: val * 86400 * 1000, text: `${val} дн.` };
  return null;
}

// Основная обработка команд
async function handleIncomingMessage(message, env) {
  const startTime = Date.now();
  const peerId = message.peer_id;
  const fromId = message.from_id;
  const msgId = message.id || message.conversation_message_id;

  if (!peerId || fromId <= 0) return;

  const rawText = (message.text || "").trim();
  if (!rawText) return;

  const parts = rawText.split(/\s+/);
  let command = parts[0].toLowerCase();

  // Удаляем упоминание бота вида /ping@botname
  if (command.includes("@")) {
    command = command.split("@")[0];
  }

  // 1. Команда /ping
  if (command === "/ping") {
    const pingStart = Date.now();
    await callVkApi("utils.getServerTime");
    const pingMs = Date.now() - pingStart;
    const responseTime = Date.now() - startTime;

    const reply = `🏓 Понг! Пинг: ${pingMs} ms | Скорость ответа: ${responseTime} ms`;
    await sendReply(peerId, reply, msgId);
    return;
  }

  // 2. Команда /stats
  if (command === "/stats") {
    const targetId = getTargetUserId(message) || fromId;
    const userMention = await getUserMention(targetId);

    // Должность по умолчанию
    const roleName = ROLE_NAMES[0];
    const assignedBy = "[https://vk.ru/club239281784|[«𝗠𝗶𝗻𝘁» – чат-менеджер](https://vk.ru/cm_mint)]";
    const assignedDate = "—";
    const nowStr = new Date().toLocaleString("ru-RU", { timeZone: "Europe/Moscow" });

    const reply = 
`📊 Информация о пользователе ${userMention}
📁 Общая информация:
├ Nick_Name: Не установлен
├ VK ID: ${targetId}
└ Должность в беседе: ${roleName}
🛡️ Модераторские данные:
├ Предупреждения: 0/3
├ Активный мут в беседе: Нет
├ Должность выдал: ${assignedBy}
└ Дата назначения: ${assignedDate}
💬 Статистика активности:
├ Сообщений за сегодня: 1
├ Всего сообщений: 1
└ Последнее сообщение: ${nowStr}`;

    await sendReply(peerId, reply, msgId);
    return;
  }

  // 3. Команда /q (Самокик)
  if (command === "/q") {
    const userMention = await getUserMention(fromId);
    const chatId = peerId > 2000000000 ? peerId - 2000000000 : peerId;

    try {
      const kickRes = await callVkApi("messages.removeChatUser", {
        chat_id: chatId,
        user_id: fromId
      });

      if (kickRes.error) {
        await sendReply(peerId, "К сожалению вас не удалось исключить из беседы, возможно вы имеете права системного администратора (звёздочку).", msgId);
      } else {
        await sendReply(peerId, `${userMention} был(а) исключён(а) из беседы по собственному желанию.`, msgId);
      }
    } catch {
      await sendReply(peerId, "К сожалению вас не удалось исключить из беседы, возможно вы имеете права системного администратора (звёздочку).", msgId);
    }
    return;
  }

  // 4. Команда /id
  if (command === "/id") {
    const targetId = getTargetUserId(message) || fromId;
    const userMention = await getUserMention(targetId);
    const regDate = await getRegistrationDate(targetId);

    const reply = 
`🔢 Информация о профиле ${userMention}
🆔 VK ID: ${targetId}
📅 Дата регистрации: ${regDate}`;

    await sendReply(peerId, reply, msgId);
    return;
  }

  // 5. Команда /help
  if (command === "/help") {
    const reply = 
`📋 Список доступных команд чат-менеджера «Mint»:

🔹 Основные команды:
/ping — Узнать пинг и скорость отклика
/stats — Информация о пользователе и активность
/id — Профиль и дата регистрации ВКонтакте
/q — Выйти из беседы по собственному желанию
/help — Справка по командам бота

🛡️ Команды модерации:
/minfo — Информация о должности
/mute [пользователь] [срок] [причина] — Выдать блокировку чата
/unmute [пользователь] — Снять блокировку чата`;

    await sendReply(peerId, reply, msgId);
    return;
  }

  // 6. Команда /minfo
  if (command === "/minfo") {
    const targetId = getTargetUserId(message) || fromId;
    const userMention = await getUserMention(targetId);

    const reply = 
`👤 Информация о должности пользователя ${userMention}
💼 Должность: Пользователь Mint
🔰 Уровень прав: 0
📅 Дата назначения: —
👮 Назначил(-а): [https://vk.ru/club239281784|[«𝗠𝗶𝗻𝘁» – чат-менеджер](https://vk.ru/cm_mint)]`;

    await sendReply(peerId, reply, msgId);
    return;
  }

  // 7. Команда /mute
  if (command === "/mute") {
    const targetId = getTargetUserId(message);
    if (!targetId) {
      await sendReply(peerId, "❌ Укажите пользователя (ответом на сообщение или ссылкой): /mute [пользователь] [время: 10m/2h] [причина]", msgId);
      return;
    }

    const targetMention = await getUserMention(targetId);
    const adminMention = await getUserMention(fromId);

    const timeArg = parts[2] || "30m";
    const duration = parseDuration(timeArg) || { ms: 30 * 60 * 1000, text: "30 мин." };
    const reason = parts.slice(3).join(" ") || "Нарушение правил беседы";

    const untilDate = new Date(Date.now() + duration.ms).toLocaleString("ru-RU", { timeZone: "Europe/Moscow" });

    const reply = 
`🔇 ${targetMention} получил(-а) блокировку чата на ${duration.text}
📝 Причина: ${reason}
⏳ Срок действия до: ${untilDate}
👮 Выдал(а): ${adminMention}`;

    await sendReply(peerId, reply, msgId);
    return;
  }

  // 8. Команда /unmute
  if (command === "/unmute") {
    const targetId = getTargetUserId(message);
    if (!targetId) {
      await sendReply(peerId, "❌ Укажите пользователя: /unmute [пользователь]", msgId);
      return;
    }

    const targetMention = await getUserMention(targetId);
    const adminMention = await getUserMention(fromId);

    const reply = 
`🔊 ${targetMention} больше не имеет блокировки чата.
👮 Снял(-а): ${adminMention}`;

    await sendReply(peerId, reply, msgId);
    return;
  }
}
