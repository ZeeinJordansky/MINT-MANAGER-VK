const VK_ACCESS_TOKEN = "vk1.a.ChdOUA-5nsExGGSiBzaKdFc-jNsQYuYnmoRkzgtfn7K5wB0QB175HBfyw5H2JZ7Cm7ZI_NxSXdsPZGsclZ8SpcyaCNGc90bnjwtjPR2FStGjd4joB-3K0Rq6NtLJSuzchROf3lUuN4wnKDYYG5wSx72Zc22AhQk1fani_3SkB40UJXpNTsmMEZCtMX84Cks6Vgb9BKDaUhZo1BcIz4VbRA";
const VK_CONFIRMATION_CODE = "ce6d834c";
const VK_API_VERSION = "5.199";

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

// Функция склонения дней
function pluralDays(n) {
  const abs = Math.abs(n) % 100;
  const num = abs % 10;
  if (abs > 10 && abs < 20) return `${n} дней`;
  if (num > 1 && num < 5) return `${n} дня`;
  if (num === 1) return `${n} день`;
  return `${n} дней`;
}

// In-Memory кэш для Cloudflare Worker среды
const memoryStore = {
  members: new Map(), // key: `${peerId}_${userId}`
  warns: new Map(),   // key: `${peerId}_${userId}` -> []
  messages: new Map() // key: peerId -> [{id, conversation_message_id, from_id, date}]
};

function getMember(peerId, userId) {
  const key = `${peerId}_${userId}`;
  if (!memoryStore.members.has(key)) {
    memoryStore.members.set(key, {
      peerId,
      userId,
      role: 0,
      nick: null,
      warns: 0,
      mutedUntil: null,
      muteReason: null,
      mutedBy: null,
      bannedUntil: null,
      banReason: null,
      bannedBy: null,
      assignedBy: null,
      assignedAt: null,
      todayCount: 1,
      totalCount: 1,
      lastMsgDate: new Date()
    });
  }
  return memoryStore.members.get(key);
}

export async function onRequest(context) {
  const { request, env } = context;

  if (request.method !== "POST") {
    return new Response(VK_CONFIRMATION_CODE, {
      status: 200,
      headers: { "Content-Type": "text/plain; charset=utf-8" }
    });
  }

  try {
    const rawBody = await request.text();
    if (!rawBody) return new Response("ok", { status: 200, headers: { "Content-Type": "text/plain; charset=utf-8" } });

    const data = JSON.parse(rawBody);

    // Подтверждение сервера Callback API
    if (data.type === "confirmation") {
      return new Response(VK_CONFIRMATION_CODE, {
        status: 200,
        headers: { "Content-Type": "text/plain; charset=utf-8" }
      });
    }

    // Обработка клика по Callback кнопке (message_event)
    if (data.type === "message_event") {
      if (context.waitUntil) {
        context.waitUntil(handleMessageEvent(data.object, env));
      } else {
        await handleMessageEvent(data.object, env);
      }
      return new Response("ok", { status: 200, headers: { "Content-Type": "text/plain; charset=utf-8" } });
    }

    // Обработка входящего сообщения (message_new)
    if (data.type === "message_new") {
      const message = data.object?.message || data.object;
      if (message) {
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
      url.searchParams.set(key, typeof value === "object" ? JSON.stringify(value) : value.toString());
    }
  }

  const res = await fetch(url.toString());
  return await res.json();
}

// Отправка ответа с обязательной репликой (reply)
async function sendReply(peerId, messageText, messageObj, keyboard = null) {
  try {
    const params = {
      peer_id: peerId,
      message: messageText,
      random_id: Math.floor(Math.random() * 2000000000)
    };

    if (keyboard) {
      params.keyboard = keyboard;
    }

    // Самое надежное: forward с исчерпывающим JSON
    const cmId = messageObj.conversation_message_id;
    const msgId = messageObj.id;

    if (cmId && cmId > 0) {
      params.forward = JSON.stringify({
        peer_id: peerId,
        conversation_message_ids: [cmId],
        is_reply: 1
      });
    } else if (msgId && msgId > 0) {
      params.reply_to = msgId;
    }

    let result = await callVkApi("messages.send", params);

    // Если VK ругнулся на forward/reply_to, делаем фоллбек с чистым reply_to или без
    if (result.error) {
      delete params.forward;
      if (msgId && msgId > 0) {
        params.reply_to = msgId;
      }
      result = await callVkApi("messages.send", params);
      if (result.error) {
        delete params.reply_to;
        result = await callVkApi("messages.send", params);
      }
    }
    return result;
  } catch (e) {
    console.error("sendReply error:", e);
  }
}

// Редактирование существующего сообщения бота (при нажатии callback-кнопок)
async function editBotMessage(peerId, conversationMessageId, text, keyboard = null) {
  try {
    const params = {
      peer_id: peerId,
      conversation_message_id: conversationMessageId,
      message: text,
      keep_forward_messages: 1
    };
    if (keyboard) {
      params.keyboard = keyboard;
    }
    return await callVkApi("messages.edit", params);
  } catch (e) {
    console.error("editBotMessage error:", e);
  }
}

// Упоминание пользователя
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

// Дата регистрации через regvk.com
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

// Определение целевого пользователя (reply, fwd, упоминание)
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
  if (!str) return null;
  const match = str.toLowerCase().match(/^(\d+)([smhd])$/);
  if (!match) return null;
  const val = parseInt(match[1], 10);
  const unit = match[2];
  if (unit === "s") return { ms: val * 1000, text: `${val} сек.` };
  if (unit === "m") return { ms: val * 60 * 1000, text: `${val} мин.` };
  if (unit === "h") return { ms: val * 3600 * 1000, text: `${val} ч.` };
  if (unit === "d") return { ms: val * 86400 * 1000, text: pluralDays(val) };
  return null;
}

// Генератор callback-кнопки
function cbBtn(label, payload, color = "secondary") {
  return {
    action: {
      type: "callback",
      label: label,
      payload: JSON.stringify(payload)
    },
    color: color // secondary = белый, negative = красный, primary = синий, positive = зеленый
  };
}

// Построение Inline-клавиатуры
function inlineKeyboard(rows) {
  return {
    inline: true,
    buttons: rows
  };
}

// ==========================================
// ОСНОВНАЯ ОБРАБОТКА ВХОДЯЩИХ СООБЩЕНИЙ
// ==========================================
async function handleIncomingMessage(message, env) {
  const startTime = Date.now();
  const peerId = message.peer_id;
  const fromId = message.from_id;

  if (!peerId || fromId <= 0) return;

  // Кэшируем сообщение для последующей работы команды /clear
  if (!memoryStore.messages.has(peerId)) {
    memoryStore.messages.set(peerId, []);
  }
  const chatMsgs = memoryStore.messages.get(peerId);
  chatMsgs.push({
    id: message.id,
    cmId: message.conversation_message_id,
    fromId: fromId,
    date: Date.now()
  });
  if (chatMsgs.length > 500) chatMsgs.shift();

  // Получаем участника из хранилища
  const member = getMember(peerId, fromId);
  member.totalCount++;
  member.todayCount++;
  member.lastMsgDate = new Date();

  // Проверка активного мута: удаляем сообщение
  if (member.mutedUntil && member.mutedUntil > Date.now()) {
    try {
      if (message.conversation_message_id) {
        await callVkApi("messages.delete", {
          peer_id: peerId,
          conversation_message_ids: [message.conversation_message_id],
          delete_for_all: 1
        });
      }
    } catch {}
    return;
  }

  const rawText = (message.text || "").trim();
  if (!rawText) return;

  const parts = rawText.split(/\s+/);
  let command = parts[0].toLowerCase();
  if (command.includes("@")) {
    command = command.split("@")[0];
  }

  // 1. /ping
  if (command === "/ping") {
    const pingStart = Date.now();
    await callVkApi("utils.getServerTime");
    const pingMs = Date.now() - pingStart;
    const responseTime = Date.now() - startTime;

    const reply = `🏓 Понг! Пинг: ${pingMs} ms | Скорость ответа: ${responseTime} ms`;
    await sendReply(peerId, reply, message);
    return;
  }

  // 2. /stats
  if (command === "/stats") {
    const targetId = getTargetUserId(message) || fromId;
    const targetMember = getMember(peerId, targetId);
    const userMention = await getUserMention(targetId);

    const nick = targetMember.nick || "Не установлен";
    const roleName = ROLE_NAMES[targetMember.role] || ROLE_NAMES[0];
    const isMuted = (targetMember.mutedUntil && targetMember.mutedUntil > Date.now()) ? "Да" : "Нет";

    let assignedByText = "[https://vk.ru/club239281784|[«𝗠𝗶𝗻𝘁» – чат-менеджер](https://vk.ru/cm_mint)]";
    if (targetMember.assignedBy && targetMember.role > 0) {
      assignedByText = await getUserMention(targetMember.assignedBy);
    }

    const assignedDateText = targetMember.assignedAt 
      ? new Date(targetMember.assignedAt).toLocaleString("ru-RU", { timeZone: "Europe/Moscow" })
      : "—";

    const lastMsgText = new Date(targetMember.lastMsgDate).toLocaleString("ru-RU", { timeZone: "Europe/Moscow" });

    const reply = 
`📊 Информация о пользователе ${userMention}
📁 Общая информация:
├ Nick_Name: ${nick}
├ VK ID: ${targetId}
└ Должность в беседе: ${roleName}
🛡️ Модераторские данные:
├ Предупреждения: ${targetMember.warns}/3
├ Активный мут в беседе: ${isMuted}
├ Должность выдал: ${assignedByText}
└ Дата назначения: ${assignedDateText}
💬 Статистика активности:
├ Сообщений за сегодня: ${targetMember.todayCount}
├ Всего сообщений: ${targetMember.totalCount}
└ Последнее сообщение: ${lastMsgText}`;

    await sendReply(peerId, reply, message);
    return;
  }

  // 3. /q
  if (command === "/q") {
    const userMention = await getUserMention(fromId);
    const chatId = peerId > 2000000000 ? peerId - 2000000000 : peerId;

    try {
      const kickRes = await callVkApi("messages.removeChatUser", {
        chat_id: chatId,
        user_id: fromId
      });

      if (kickRes.error) {
        await sendReply(peerId, "К сожалению вас не удалось исключить из беседы, возможно вы имеете права системного администратора (звёздочку).", message);
      } else {
        const hadRoleOrNick = (member.role > 0) || (member.nick !== null);
        member.role = 0;
        member.nick = null;
        member.assignedBy = null;
        member.assignedAt = null;

        let reply = `${userMention} был(а) исключён(а) из беседы по собственному желанию.`;
        if (hadRoleOrNick) {
          reply += "\n💬 Также должность и Nick у пользователя были сняты.";
        }
        await sendReply(peerId, reply, message);
      }
    } catch {
      await sendReply(peerId, "К сожалению вас не удалось исключить из беседы, возможно вы имеете права системного администратора (звёздочку).", message);
    }
    return;
  }

  // 4. /id
  if (command === "/id") {
    const targetId = getTargetUserId(message) || fromId;
    const userMention = await getUserMention(targetId);
    const regDate = await getRegistrationDate(targetId);

    const reply = 
`🔢 Информация о профиле ${userMention}
🆔 VK ID: ${targetId}
📅 Дата регистрации: ${regDate}`;

    await sendReply(peerId, reply, message);
    return;
  }

  // 5. /help
  if (command === "/help") {
    const reply = 
`📋 Список доступных команд чат-менеджера «Mint»:

🔹 Основные команды (для всех пользователей):
/ping — Проверить пинг и скорость отклика бота
/stats — Просмотр своей статистики или упомянутого пользователя
/id — Информация о профиле и дате регистрации ВКонтакте
/q — Выйти из беседы по собственному желанию
/staff — Список модераторов и администрации беседы
/help — Справка по командам бота

🛡️ Команды модерации:
/minfo — Информация о своей должности и правах
/mute [пользователь] [срок] [причина] — Заблокировать отправку сообщений
/unmute [пользователь] — Снять блокировку чата
/clear [пользователь] [кол-во] — Очистить сообщения нарушителя
/kick [пользователь] [причина] — Исключить пользователя из беседы
/warn [пользователь] [срок: дни] [причина] — Выдать предупреждение
/unwarn [пользователь] — Снять предупреждение
/get [пользователь] — Карточка наказаний с интерактивными кнопками
/setnick [пользователь] [ник] — Установить ник
/rnick [пользователь] — Удалить ник
/gnick [пользователь] — Узнать ник
/nlist — Список участников беседы с никами

🔨 Для старших модераторов:
/ban [пользователь] [срок: дни] [причина] — Заблокировать в беседе
/unban [пользователь] — Разблокировать в беседе
/list — Интерактивный список всех нарушителей беседы`;

    await sendReply(peerId, reply, message);
    return;
  }

  // 6. /staff
  if (command === "/staff") {
    // Собираем всех членов беседы с должностью > 0
    const staffMembers = [];
    for (const [key, m] of memoryStore.members.entries()) {
      if (key.startsWith(`${peerId}_`) && m.role > 0) {
        staffMembers.push(m);
      }
    }

    const owners = staffMembers.filter(m => m.role === 7);
    const leaders = staffMembers.filter(m => m.role === 6);
    const chiefAdmins = staffMembers.filter(m => m.role === 5);
    const srAdmins = staffMembers.filter(m => m.role === 4);
    const admins = staffMembers.filter(m => m.role === 3);
    const srMods = staffMembers.filter(m => m.role === 2);
    const mods = staffMembers.filter(m => m.role === 1);

    async function formatList(arr) {
      if (!arr || arr.length === 0) return "— Отсутствуют";
      const names = await Promise.all(arr.map(x => getUserMention(x.userId)));
      return names.map(n => `— ${n}`).join("\n");
    }

    const ownersStr = await formatList(owners);
    const leadersStr = await formatList(leaders);
    const chiefAdminStr = chiefAdmins.length > 0 ? await getUserMention(chiefAdmins[0].userId) : "Отсутствует";
    const srAdminStr = srAdmins.length > 0 ? await getUserMention(srAdmins[0].userId) : "Отсутствует";
    const adminStr = admins.length > 0 ? await getUserMention(admins[0].userId) : "Отсутствует";
    const srModStr = srMods.length > 0 ? await getUserMention(srMods[0].userId) : "Отсутствует";
    const modStr = mods.length > 0 ? await getUserMention(mods[0].userId) : "Отсутствует";

    const reply = 
`🛡️ Модерация беседы
👑 Владельцы беседы:
${ownersStr}
💎 Руководители беседы:
${leadersStr}
⚜️ Администраторы:
├ Главный администратор — ${chiefAdminStr}
├ Старший администратор — ${srAdminStr}
└ Администратор — ${adminStr}
🔨 Модераторы:
├ Старший модератор — ${srModStr}
└ Модератор — ${modStr}
📊 Всего модераторов: ${staffMembers.length}`;

    const kb = inlineKeyboard([
      [cbBtn("📋 Список участников с никами", { act: "nlist", page: 1, peer: peerId }, "secondary")]
    ]);

    await sendReply(peerId, reply, message, kb);
    return;
  }

  // 7. /minfo
  if (command === "/minfo") {
    const targetId = getTargetUserId(message) || fromId;
    const targetMember = getMember(peerId, targetId);

    if (targetMember.role < 1 && targetId === fromId) {
      await sendReply(peerId, "❌ Данная команда доступна только модераторам и выше.", message);
      return;
    }

    const userMention = await getUserMention(targetId);
    const assignedBy = targetMember.assignedBy 
      ? await getUserMention(targetMember.assignedBy)
      : "[https://vk.ru/club239281784|[«𝗠𝗶𝗻𝘁» – чат-менеджер](https://vk.ru/cm_mint)]";
    const assignedDate = targetMember.assignedAt
      ? new Date(targetMember.assignedAt).toLocaleString("ru-RU", { timeZone: "Europe/Moscow" })
      : "—";

    const reply = 
`👤 Информация о должности пользователя ${userMention}
💼 Должность: ${ROLE_NAMES[targetMember.role] || ROLE_NAMES[0]}
🔰 Уровень прав: ${targetMember.role}
📅 Дата назначения: ${assignedDate}
👮 Назначил(а): ${assignedBy}`;

    await sendReply(peerId, reply, message);
    return;
  }

  // 8. /mute
  if (command === "/mute") {
    if (member.role < 1) {
      await sendReply(peerId, "❌ Команда доступна с должности Модератор и выше.", message);
      return;
    }

    const targetId = getTargetUserId(message);
    if (!targetId) {
      await sendReply(peerId, "❌ Укажите пользователя (ответом или ссылкой): /mute [пользователь] [время: 10m/2h/1d] [причина]", message);
      return;
    }

    const targetMember = getMember(peerId, targetId);
    if (targetMember.role >= member.role && member.role !== 7) {
      await sendReply(peerId, "❌ Вы не можете замутить пользователя с равной или более высокой должностью.", message);
      return;
    }

    const args = parts.slice(1).filter(a => !a.startsWith("[id") && !a.startsWith("@") && !a.includes("vk."));
    const duration = parseDuration(args[0]) || { ms: 30 * 60 * 1000, text: "30 мин." };
    if (parseDuration(args[0])) args.shift();
    const reason = args.join(" ") || "Нарушение правил беседы";

    const untilDate = new Date(Date.now() + duration.ms);
    targetMember.mutedUntil = untilDate.getTime();
    targetMember.muteReason = reason;
    targetMember.mutedBy = fromId;

    const targetMention = await getUserMention(targetId);
    const adminMention = await getUserMention(fromId);
    const untilDateStr = untilDate.toLocaleString("ru-RU", { timeZone: "Europe/Moscow" });

    const reply = 
`🔇 ${targetMention} не сможет писать в эту беседу до ${untilDateStr}, так как модератор ${adminMention} выдал(а) ему мут сроком на ${duration.text}.
📝 Причина мута: ${reason}`;

    const kb = inlineKeyboard([
      [
        cbBtn("🔊 Снять мут", { act: "unmute", target: targetId, peer: peerId }, "secondary"),
        cbBtn("🧹 Очистить сообщения", { act: "clear", target: targetId, count: 20, peer: peerId }, "secondary")
      ]
    ]);

    await sendReply(peerId, reply, message, kb);
    return;
  }

  // 9. /unmute
  if (command === "/unmute") {
    if (member.role < 1) {
      await sendReply(peerId, "❌ Команда доступна с должности Модератор и выше.", message);
      return;
    }

    const targetId = getTargetUserId(message);
    if (!targetId) {
      await sendReply(peerId, "❌ Укажите пользователя: /unmute [пользователь]", message);
      return;
    }

    const targetMember = getMember(peerId, targetId);
    targetMember.mutedUntil = null;
    targetMember.muteReason = null;
    targetMember.mutedBy = null;

    const targetMention = await getUserMention(targetId);
    const adminMention = await getUserMention(fromId);

    const reply = `🔊 ${targetMention} больше не имеет блокировки чата, так как она была снята модератором ${adminMention}`;

    const kb = inlineKeyboard([
      [cbBtn("🧹 Очистить сообщения", { act: "clear", target: targetId, count: 20, peer: peerId }, "secondary")]
    ]);

    await sendReply(peerId, reply, message, kb);
    return;
  }

  // 10. /clear
  if (command === "/clear") {
    if (member.role < 1) {
      await sendReply(peerId, "❌ Команда доступна с должности Модератор и выше.", message);
      return;
    }

    const targetId = getTargetUserId(message);
    const countArg = parseInt(parts[parts.length - 1], 10);
    const count = !isNaN(countArg) && countArg > 0 ? Math.min(countArg, 100) : 20;

    const targetMention = await getUserMention(targetId || fromId);
    const adminMention = await getUserMention(fromId);

    // Удаляем сообщения пользователя из истории беседы
    const chatMsgs = memoryStore.messages.get(peerId) || [];
    const toDelete = chatMsgs.filter(m => (!targetId || m.fromId === targetId)).slice(-count);
    const cmIds = toDelete.map(m => m.cmId).filter(Boolean);

    if (cmIds.length > 0) {
      try {
        await callVkApi("messages.delete", {
          peer_id: peerId,
          conversation_message_ids: cmIds,
          delete_for_all: 1
        });
      } catch {}
    }

    const reply = `🧹 Сообщения от ${targetMention} были успешно очищены модератором ${adminMention}`;
    await sendReply(peerId, reply, message);
    return;
  }

  // 11. /kick
  if (command === "/kick") {
    if (member.role < 1) {
      await sendReply(peerId, "❌ Команда доступна с должности Модератор и выше.", message);
      return;
    }

    const targetId = getTargetUserId(message);
    if (!targetId) {
      await sendReply(peerId, "❌ Укажите пользователя: /kick [пользователь] [причина]", message);
      return;
    }

    const targetMember = getMember(peerId, targetId);
    if (targetMember.role >= member.role && member.role !== 7) {
      await sendReply(peerId, "❌ Вы не можете исключить пользователя с равной или более высокой должностью.", message);
      return;
    }

    const reason = parts.slice(1).filter(a => !a.startsWith("[id") && !a.startsWith("@") && !a.includes("vk.")).join(" ") || "Нарушение правил";

    const chatId = peerId > 2000000000 ? peerId - 2000000000 : peerId;
    await callVkApi("messages.removeChatUser", {
      chat_id: chatId,
      user_id: targetId
    });

    const targetMention = await getUserMention(targetId);
    const adminMention = await getUserMention(fromId);

    const reply = 
`👢 ${targetMention} исключён(а) из беседы модератором ${adminMention}.
📝 Причина кика: ${reason}
💬 Пользователя можно вернуть в беседу через приглашение.`;

    const kb = inlineKeyboard([
      [
        cbBtn("🧹 Очистить сообщения", { act: "clear", target: targetId, count: 20, peer: peerId }, "secondary"),
        cbBtn("🔒 Заблокировать в беседе", { act: "ban", target: targetId, peer: peerId, reason: reason }, "secondary")
      ]
    ]);

    await sendReply(peerId, reply, message, kb);
    return;
  }

  // 12. /warn
  if (command === "/warn") {
    if (member.role < 1) {
      await sendReply(peerId, "❌ Команда доступна с должности Модератор и выше.", message);
      return;
    }

    const targetId = getTargetUserId(message);
    if (!targetId) {
      await sendReply(peerId, "❌ Укажите пользователя: /warn [пользователь] [срок: дни] [причина]", message);
      return;
    }

    const targetMember = getMember(peerId, targetId);
    if (targetMember.role >= member.role && member.role !== 7) {
      await sendReply(peerId, "❌ Вы не можете выдать варн пользователю с равной или более высокой должностью.", message);
      return;
    }

    const args = parts.slice(1).filter(a => !a.startsWith("[id") && !a.startsWith("@") && !a.includes("vk."));
    let days = null;
    if (args.length > 0 && /^\d+$/.test(args[0])) {
      days = parseInt(args[0], 10);
      args.shift();
    }
    const reason = args.join(" ") || "Нарушение правил беседы";

    targetMember.warns++;
    const expiresAt = days ? Date.now() + days * 86400 * 1000 : null;

    if (!memoryStore.warns.has(`${peerId}_${targetId}`)) {
      memoryStore.warns.set(`${peerId}_${targetId}`, []);
    }
    memoryStore.warns.get(`${peerId}_${targetId}`).push({
      reason,
      issuedBy: fromId,
      expiresAt,
      issuedAt: Date.now()
    });

    const targetMention = await getUserMention(targetId);
    const adminMention = await getUserMention(fromId);
    const daysStr = days ? `через ${pluralDays(days)}` : "никогда";

    const reply = 
`⚠️ ${targetMention} теперь имеет ${targetMember.warns} предупреждений в этой беседе, так как модератор ${adminMention} выдал ему предупреждение.
📝 Причина варна: ${reason}
💬 Предупреждение будет снято ${daysStr}`;

    const kb = inlineKeyboard([
      [
        cbBtn("⚠️ Снять предупреждение", { act: "unwarn", target: targetId, peer: peerId }, "secondary"),
        cbBtn("🧹 Очистить сообщения", { act: "clear", target: targetId, count: 20, peer: peerId }, "secondary")
      ]
    ]);

    await sendReply(peerId, reply, message, kb);
    return;
  }

  // 13. /unwarn
  if (command === "/unwarn") {
    if (member.role < 1) {
      await sendReply(peerId, "❌ Команда доступна с должности Модератор и выше.", message);
      return;
    }

    const targetId = getTargetUserId(message);
    if (!targetId) {
      await sendReply(peerId, "❌ Укажите пользователя: /unwarn [пользователь]", message);
      return;
    }

    const targetMember = getMember(peerId, targetId);
    if (targetMember.warns > 0) targetMember.warns--;

    const warnList = memoryStore.warns.get(`${peerId}_${targetId}`) || [];
    if (warnList.length > 0) warnList.pop();

    const targetMention = await getUserMention(targetId);
    const adminMention = await getUserMention(fromId);

    const reply = `⚠️ ${targetMention} теперь имеет ${targetMember.warns} активных предупреждений, так как модератором ${adminMention} было снято 1 предупреждение..`;
    await sendReply(peerId, reply, message);
    return;
  }

  // 14. /get
  if (command === "/get") {
    if (member.role < 1) {
      await sendReply(peerId, "❌ Команда доступна с должности Модератор и выше.", message);
      return;
    }

    const targetId = getTargetUserId(message) || fromId;
    const targetMember = getMember(peerId, targetId);
    const targetMention = await getUserMention(targetId);

    const isMuted = (targetMember.mutedUntil && targetMember.mutedUntil > Date.now());
    const muteTime = isMuted 
      ? new Date(targetMember.mutedUntil).toLocaleString("ru-RU", { timeZone: "Europe/Moscow" })
      : "нет";

    const isBanned = (targetMember.bannedUntil && targetMember.bannedUntil > Date.now());
    const banTime = isBanned
      ? (targetMember.bannedUntil > Date.now() + 1000 * 86400 * 3650 ? "бессрочно" : new Date(targetMember.bannedUntil).toLocaleString("ru-RU", { timeZone: "Europe/Moscow" }))
      : "нет";

    const reply = 
`📋 Информация о наказаниях ${targetMention}
🛡️ Активные наказания:
├ Активных предупреждений: ${targetMember.warns}
├ Блокировка чата: ${isMuted ? "есть" : "нет"} | ${muteTime}
└ Блокировка в беседе: ${isBanned ? "есть" : "нет"} | ${banTime}
Для подробной информации, нажмите на кнопку:`;

    const kb = inlineKeyboard([
      [
        cbBtn("🔇 Мут", { act: "get_page", tab: "mute", target: targetId, peer: peerId }, "secondary"),
        cbBtn("⚠️ Варны", { act: "get_page", tab: "warns", target: targetId, peer: peerId }, "secondary"),
        cbBtn("🔒 Бан в беседе", { act: "get_page", tab: "ban", target: targetId, peer: peerId }, "secondary")
      ]
    ]);

    await sendReply(peerId, reply, message, kb);
    return;
  }

  // 15. /setnick
  if (command === "/setnick") {
    if (member.role < 1) {
      await sendReply(peerId, "❌ Команда доступна с должности Модератор и выше.", message);
      return;
    }

    const targetId = getTargetUserId(message);
    if (!targetId) {
      await sendReply(peerId, "❌ Укажите пользователя: /setnick [пользователь] [новый ник]", message);
      return;
    }

    const nickArgs = parts.slice(1).filter(a => !a.startsWith("[id") && !a.startsWith("@") && !a.includes("vk."));
    const newNick = nickArgs.join(" ") || "Без ника";

    const targetMember = getMember(peerId, targetId);
    targetMember.nick = newNick;

    const targetMention = await getUserMention(targetId);
    const adminMention = await getUserMention(fromId);

    const reply = 
`✏️ Для ${targetMention} установлен новый ник модератором ${adminMention}
📝 Nick_Name: ${newNick}`;

    const kb = inlineKeyboard([
      [
        cbBtn("✏️ Удалить ник", { act: "rnick", target: targetId, peer: peerId }, "secondary"),
        cbBtn("📋 Список участников с никами", { act: "nlist", page: 1, peer: peerId }, "secondary")
      ]
    ]);

    await sendReply(peerId, reply, message, kb);
    return;
  }

  // 16. /rnick
  if (command === "/rnick") {
    if (member.role < 1) {
      await sendReply(peerId, "❌ Команда доступна с должности Модератор и выше.", message);
      return;
    }

    const targetId = getTargetUserId(message);
    if (!targetId) {
      await sendReply(peerId, "❌ Укажите пользователя: /rnick [пользователь]", message);
      return;
    }

    const targetMember = getMember(peerId, targetId);
    targetMember.nick = null;

    const targetMention = await getUserMention(targetId);
    const adminMention = await getUserMention(fromId);

    const reply = `✏️ Для ${targetMention} удалён ник модератором ${adminMention}.`;

    const kb = inlineKeyboard([
      [cbBtn("📋 Список участников с никами", { act: "nlist", page: 1, peer: peerId }, "secondary")]
    ]);

    await sendReply(peerId, reply, message, kb);
    return;
  }

  // 17. /gnick
  if (command === "/gnick") {
    const targetId = getTargetUserId(message) || fromId;
    const targetMember = getMember(peerId, targetId);
    const targetMention = await getUserMention(targetId);

    const nick = targetMember.nick || "Не установлен";
    const adminMention = targetMember.assignedBy 
      ? await getUserMention(targetMember.assignedBy)
      : "[https://vk.ru/club239281784|[«𝗠𝗶𝗻𝘁» – чат-менеджер](https://vk.ru/cm_mint)]";

    const reply = 
`✏️ Ник пользователя ${targetMention}: ${nick}
💬 Ник был установлен модератором ${adminMention}`;

    await sendReply(peerId, reply, message);
    return;
  }

  // 18. /nlist
  if (command === "/nlist") {
    await renderNlistPage(peerId, 1, message);
    return;
  }

  // 19. /ban
  if (command === "/ban") {
    if (member.role < 2) {
      await sendReply(peerId, "❌ Команда доступна с должности Старший модератор и выше.", message);
      return;
    }

    const targetId = getTargetUserId(message);
    if (!targetId) {
      await sendReply(peerId, "❌ Укажите пользователя: /ban [пользователь] [срок: дни] [причина]", message);
      return;
    }

    const targetMember = getMember(peerId, targetId);
    if (targetMember.role >= member.role && member.role !== 7) {
      await sendReply(peerId, "❌ Вы не можете заблокировать пользователя с равной или более высокой должностью.", message);
      return;
    }

    const args = parts.slice(1).filter(a => !a.startsWith("[id") && !a.startsWith("@") && !a.includes("vk."));
    let days = null;
    if (args.length > 0 && /^\d+$/.test(args[0])) {
      days = parseInt(args[0], 10);
      args.shift();
    }
    const reason = args.join(" ") || "Нарушение правил беседы";

    const untilMs = days ? Date.now() + days * 86400 * 1000 : Date.now() + 1000 * 86400 * 3650;
    targetMember.bannedUntil = untilMs;
    targetMember.banReason = reason;
    targetMember.bannedBy = fromId;

    // Исключаем из беседы
    const chatId = peerId > 2000000000 ? peerId - 2000000000 : peerId;
    await callVkApi("messages.removeChatUser", {
      chat_id: chatId,
      user_id: targetId
    });

    const targetMention = await getUserMention(targetId);
    const adminMention = await getUserMention(fromId);
    const timeStr = days ? `через ${pluralDays(days)}` : "никогда";

    const reply = 
`🔒 ${targetMention} теперь заблокирован в этой беседе модератором ${adminMention}.
📝 Причина: ${reason}
💬 Блокировка будет снята ${timeStr}.`;

    const kb = inlineKeyboard([
      [
        cbBtn("🔓 Разблокировать в беседе", { act: "unban", target: targetId, peer: peerId }, "secondary"),
        cbBtn("🧹 Очистить сообщения", { act: "clear", target: targetId, count: 20, peer: peerId }, "secondary")
      ]
    ]);

    await sendReply(peerId, reply, message, kb);
    return;
  }

  // 20. /unban
  if (command === "/unban") {
    if (member.role < 2) {
      await sendReply(peerId, "❌ Команда доступна с должности Старший модератор и выше.", message);
      return;
    }

    const targetId = getTargetUserId(message);
    if (!targetId) {
      await sendReply(peerId, "❌ Укажите пользователя: /unban [пользователь]", message);
      return;
    }

    const targetMember = getMember(peerId, targetId);
    targetMember.bannedUntil = null;
    targetMember.banReason = null;
    targetMember.bannedBy = null;

    const targetMention = await getUserMention(targetId);
    const adminMention = await getUserMention(fromId);

    const reply = 
`🔓 ${targetMention} больше не имеет блокировки в беседе, так как она была снята модератором ${adminMention}.
💬 Пользователя теперь можно вернуть в беседу через приглашение.`;

    await sendReply(peerId, reply, message);
    return;
  }

  // 21. /list
  if (command === "/list") {
    if (member.role < 2) {
      await sendReply(peerId, "❌ Команда доступна с должности Старший модератор и выше.", message);
      return;
    }

    const reply = 
`📋 Список наказаний в беседе
Выберите тип наказания:`;

    const kb = inlineKeyboard([
      [
        cbBtn("🔇 Муты", { act: "list_tab", tab: "mutes", page: 1, peer: peerId }, "secondary"),
        cbBtn("⚠️ Варны", { act: "list_tab", tab: "warns", page: 1, peer: peerId }, "secondary"),
        cbBtn("🔒 Блокировки в беседе", { act: "list_tab", tab: "bans", page: 1, peer: peerId }, "secondary")
      ]
    ]);

    await sendReply(peerId, reply, message, kb);
    return;
  }
}

// Рендер страницы списка участников с никами (/nlist)
async function renderNlistPage(peerId, page, message = null, editCmId = null) {
  const pageSize = 10;
  const usersWithNicks = [];

  for (const [key, m] of memoryStore.members.entries()) {
    if (key.startsWith(`${peerId}_`) && m.nick) {
      usersWithNicks.push(m);
    }
  }

  const totalPages = Math.max(1, Math.ceil(usersWithNicks.length / pageSize));
  const currentPage = Math.min(Math.max(1, page), totalPages);
  const startIdx = (currentPage - 1) * pageSize;
  const pageItems = usersWithNicks.slice(startIdx, startIdx + pageSize);

  let itemsStr = "└ Список пуст";
  if (pageItems.length > 0) {
    const lines = await Promise.all(pageItems.map(async (m, i) => {
      const mention = await getUserMention(m.userId);
      const prefix = (i === pageItems.length - 1) ? "└" : "├";
      return `${prefix} ${mention} — (${m.nick})`;
    }));
    itemsStr = lines.join("\n");
  }

  const text = 
`📋 Список участников с никами [${currentPage}/${totalPages}]
👥 Всего пользователей: ${usersWithNicks.length}
📁 Список участников:
${itemsStr}`;

  const navRow = [
    cbBtn("⏮️", { act: "nlist", page: 1, peer: peerId }, "secondary"),
    cbBtn("◀️", { act: "nlist", page: Math.max(1, currentPage - 1), peer: peerId }, "secondary"),
    cbBtn("▶️", { act: "nlist", page: Math.min(totalPages, currentPage + 1), peer: peerId }, "secondary"),
    cbBtn("⏭️", { act: "nlist", page: totalPages, peer: peerId }, "secondary")
  ];

  const kb = inlineKeyboard([navRow]);

  if (editCmId) {
    await editBotMessage(peerId, editCmId, text, kb);
  } else if (message) {
    await sendReply(peerId, text, message, kb);
  }
}

// ==========================================
// ОБРАБОТКА CALLBACK КНОПОК (MESSAGE_EVENT)
// ==========================================
async function handleMessageEvent(eventObj, env) {
  const peerId = eventObj.peer_id;
  const userId = eventObj.user_id;
  const cmId = eventObj.conversation_message_id;
  const eventId = eventObj.event_id;

  let payload = {};
  try {
    payload = typeof eventObj.payload === "string" ? JSON.parse(eventObj.payload) : eventObj.payload;
  } catch {}

  const userMember = getMember(peerId, userId);

  // Снятие мута через кнопку
  if (payload.act === "unmute") {
    if (userMember.role < 1) {
      await showEventSnackbar(eventId, peerId, userId, "❌ У вас недостаточно прав!");
      return;
    }
    const targetMember = getMember(peerId, payload.target);
    targetMember.mutedUntil = null;
    targetMember.muteReason = null;
    targetMember.mutedBy = null;

    const targetMention = await getUserMention(payload.target);
    const adminMention = await getUserMention(userId);

    const reply = `🔊 ${targetMention} больше не имеет блокировки чата, так как она была снята модератором ${adminMention}`;
    const kb = inlineKeyboard([[cbBtn("🧹 Очистить сообщения", { act: "clear", target: payload.target, count: 20, peer: peerId }, "secondary")]]);

    await editBotMessage(peerId, cmId, reply, kb);
    await showEventSnackbar(eventId, peerId, userId, "Мут успешно снят!");
    return;
  }

  // Очистка сообщений через кнопку
  if (payload.act === "clear") {
    if (userMember.role < 1) {
      await showEventSnackbar(eventId, peerId, userId, "❌ У вас недостаточно прав!");
      return;
    }
    const targetMention = await getUserMention(payload.target);
    const adminMention = await getUserMention(userId);

    const chatMsgs = memoryStore.messages.get(peerId) || [];
    const toDel = chatMsgs.filter(m => m.fromId === payload.target).slice(-payload.count);
    const cmIds = toDel.map(m => m.cmId).filter(Boolean);
    if (cmIds.length > 0) {
      try {
        await callVkApi("messages.delete", {
          peer_id: peerId,
          conversation_message_ids: cmIds,
          delete_for_all: 1
        });
      } catch {}
    }

    const reply = `🧹 Сообщения от ${targetMention} были успешно очищены модератором ${adminMention}`;
    await sendReply(peerId, reply, { conversation_message_id: cmId });
    await showEventSnackbar(eventId, peerId, userId, "Сообщения очищены!");
    return;
  }

  // Снятие варна через кнопку
  if (payload.act === "unwarn") {
    if (userMember.role < 1) {
      await showEventSnackbar(eventId, peerId, userId, "❌ У вас недостаточно прав!");
      return;
    }
    const targetMember = getMember(peerId, payload.target);
    if (targetMember.warns > 0) targetMember.warns--;

    const targetMention = await getUserMention(payload.target);
    const adminMention = await getUserMention(userId);

    const reply = `⚠️ ${targetMention} теперь имеет ${targetMember.warns} активных предупреждений, так как модератором ${adminMention} было снято 1 предупреждение..`;
    await editBotMessage(peerId, cmId, reply, null);
    await showEventSnackbar(eventId, peerId, userId, "Предупреждение снято!");
    return;
  }

  // Бан через кнопку
  if (payload.act === "ban") {
    if (userMember.role < 2) {
      await showEventSnackbar(eventId, peerId, userId, "❌ Требуется роль Старший модератор!");
      return;
    }
    const targetMember = getMember(peerId, payload.target);
    targetMember.bannedUntil = Date.now() + 1000 * 86400 * 3650;
    targetMember.banReason = payload.reason || "Нарушение";
    targetMember.bannedBy = userId;

    const chatId = peerId > 2000000000 ? peerId - 2000000000 : peerId;
    await callVkApi("messages.removeChatUser", { chat_id: chatId, user_id: payload.target });

    const targetMention = await getUserMention(payload.target);
    const adminMention = await getUserMention(userId);

    const reply = 
`🔒 ${targetMention} теперь заблокирован в этой беседе модератором ${adminMention}.
📝 Причина: ${payload.reason || "Нарушение"}
💬 Блокировка будет снята никогда.`;

    const kb = inlineKeyboard([[cbBtn("🔓 Разблокировать в беседе", { act: "unban", target: payload.target, peer: peerId }, "secondary")]]);
    await editBotMessage(peerId, cmId, reply, kb);
    await showEventSnackbar(eventId, peerId, userId, "Пользователь заблокирован!");
    return;
  }

  // Разблокировка через кнопку
  if (payload.act === "unban") {
    if (userMember.role < 2) {
      await showEventSnackbar(eventId, peerId, userId, "❌ Требуется роль Старший модератор!");
      return;
    }
    const targetMember = getMember(peerId, payload.target);
    targetMember.bannedUntil = null;
    targetMember.banReason = null;
    targetMember.bannedBy = null;

    const targetMention = await getUserMention(payload.target);
    const adminMention = await getUserMention(userId);

    const reply = 
`🔓 ${targetMention} больше не имеет блокировки в беседе, так как она была снята модератором ${adminMention}.
💬 Пользователя теперь можно вернуть в беседу через приглашение.`;

    await editBotMessage(peerId, cmId, reply, null);
    await showEventSnackbar(eventId, peerId, userId, "Пользователь разблокирован!");
    return;
  }

  // Удаление ника через кнопку
  if (payload.act === "rnick") {
    if (userMember.role < 1) {
      await showEventSnackbar(eventId, peerId, userId, "❌ Недостаточно прав!");
      return;
    }
    const targetMember = getMember(peerId, payload.target);
    targetMember.nick = null;

    const targetMention = await getUserMention(payload.target);
    const adminMention = await getUserMention(userId);

    const reply = `✏️ Для ${targetMention} удалён ник модератором ${adminMention}.`;
    const kb = inlineKeyboard([[cbBtn("📋 Список участников с никами", { act: "nlist", page: 1, peer: peerId }, "secondary")]]);
    await editBotMessage(peerId, cmId, reply, kb);
    await showEventSnackbar(eventId, peerId, userId, "Ник удален!");
    return;
  }

  // Навигация по /nlist
  if (payload.act === "nlist") {
    await renderNlistPage(peerId, payload.page, null, cmId);
    await showEventSnackbar(eventId, peerId, userId, `Страница ${payload.page}`);
    return;
  }

  // Меню карточки /get
  if (payload.act === "get_page") {
    const targetId = payload.target;
    const targetMember = getMember(peerId, targetId);
    const targetMention = await getUserMention(targetId);

    if (payload.tab === "back") {
      const isMuted = (targetMember.mutedUntil && targetMember.mutedUntil > Date.now());
      const muteTime = isMuted ? new Date(targetMember.mutedUntil).toLocaleString("ru-RU", { timeZone: "Europe/Moscow" }) : "нет";
      const isBanned = (targetMember.bannedUntil && targetMember.bannedUntil > Date.now());
      const banTime = isBanned ? "бессрочно" : "нет";

      const reply = 
`📋 Информация о наказаниях ${targetMention}
🛡️ Активные наказания:
├ Активных предупреждений: ${targetMember.warns}
├ Блокировка чата: ${isMuted ? "есть" : "нет"} | ${muteTime}
└ Блокировка в беседе: ${isBanned ? "есть" : "нет"} | ${banTime}
Для подробной информации, нажмите на кнопку:`;

      const kb = inlineKeyboard([
        [
          cbBtn("🔇 Мут", { act: "get_page", tab: "mute", target: targetId, peer: peerId }, "secondary"),
          cbBtn("⚠️ Варны", { act: "get_page", tab: "warns", target: targetId, peer: peerId }, "secondary"),
          cbBtn("🔒 Бан в беседе", { act: "get_page", tab: "ban", target: targetId, peer: peerId }, "secondary")
        ]
      ]);
      await editBotMessage(peerId, cmId, reply, kb);
      await showEventSnackbar(eventId, peerId, userId, "Главная");
      return;
    }

    if (payload.tab === "mute") {
      const isMuted = (targetMember.mutedUntil && targetMember.mutedUntil > Date.now());
      const rem = isMuted ? new Date(targetMember.mutedUntil).toLocaleString("ru-RU", { timeZone: "Europe/Moscow" }) : "не заблокирован";
      const reason = targetMember.muteReason || "отсутствует";
      const admin = targetMember.mutedBy ? await getUserMention(targetMember.mutedBy) : "—";

      const text = 
`🔇 Информация о блокировке чата ${targetMention}
📁 Информация о блокировке:
├ Срок: ${rem}
├ Причина: ${reason}
└ Выдал(а): ${admin}`;

      const kb = inlineKeyboard([[cbBtn("Назад", { act: "get_page", tab: "back", target: targetId, peer: peerId }, "negative")]]);
      await editBotMessage(peerId, cmId, text, kb);
      await showEventSnackbar(eventId, peerId, userId, "Мут");
      return;
    }

    if (payload.tab === "warns") {
      const warnList = memoryStore.warns.get(`${peerId}_${targetId}`) || [];
      let listStr = "└ Активные предупреждения отсутствуют";
      if (warnList.length > 0) {
        const lines = await Promise.all(warnList.map(async w => {
          const adm = await getUserMention(w.issuedBy);
          const exp = w.expiresAt ? new Date(w.expiresAt).toLocaleString("ru-RU", { timeZone: "Europe/Moscow" }) : "никогда";
          return `├ Причина: ${w.reason}\n├ Модератор: ${adm}\n└ Срок снятия: ${exp}`;
        }));
        listStr = lines.join("\n---\n");
      }

      const text = 
`⚠️ Предупреждения ${targetMention}
📊 Активные предупреждения: ${targetMember.warns}/3
📁 Список активных предупреждений:
${listStr}`;

      const kb = inlineKeyboard([[cbBtn("Назад", { act: "get_page", tab: "back", target: targetId, peer: peerId }, "negative")]]);
      await editBotMessage(peerId, cmId, text, kb);
      await showEventSnackbar(eventId, peerId, userId, "Варны");
      return;
    }

    if (payload.tab === "ban") {
      const isBanned = (targetMember.bannedUntil && targetMember.bannedUntil > Date.now());
      const status = isBanned ? "заблокирован(а)" : "не заблокирован(а)";
      const reason = targetMember.banReason || "отсутствует";
      const admin = targetMember.bannedBy ? await getUserMention(targetMember.bannedBy) : "—";
      const term = isBanned ? "бессрочно" : "не заблокирован";

      const text = 
`🔒 Информация о блокировке в беседе ${targetMention}
📁 Информация о блокировке:
├ Статус: ${status}
├ Причина: ${reason}
├ Выдал(а): ${admin}
└ Срок: ${term}`;

      const kb = inlineKeyboard([[cbBtn("Назад", { act: "get_page", tab: "back", target: targetId, peer: peerId }, "negative")]]);
      await editBotMessage(peerId, cmId, text, kb);
      await showEventSnackbar(eventId, peerId, userId, "Бан");
      return;
    }
  }

  // Меню списков /list
  if (payload.act === "list_tab") {
    if (payload.tab === "back") {
      const reply = `📋 Список наказаний в беседе\nВыберите тип наказания:`;
      const kb = inlineKeyboard([
        [
          cbBtn("🔇 Муты", { act: "list_tab", tab: "mutes", page: 1, peer: peerId }, "secondary"),
          cbBtn("⚠️ Варны", { act: "list_tab", tab: "warns", page: 1, peer: peerId }, "secondary"),
          cbBtn("🔒 Блокировки в беседе", { act: "list_tab", tab: "bans", page: 1, peer: peerId }, "secondary")
        ]
      ]);
      await editBotMessage(peerId, cmId, reply, kb);
      await showEventSnackbar(eventId, peerId, userId, "Список наказаний");
      return;
    }

    if (payload.tab === "mutes") {
      const mutes = [];
      for (const [key, m] of memoryStore.members.entries()) {
        if (key.startsWith(`${peerId}_`) && m.mutedUntil && m.mutedUntil > Date.now()) {
          mutes.push(m);
        }
      }

      let itemsStr = "└ Список пуст";
      if (mutes.length > 0) {
        const lines = await Promise.all(mutes.map(async m => {
          const userM = await getUserMention(m.userId);
          const adm = m.mutedBy ? await getUserMention(m.mutedBy) : "—";
          const exp = new Date(m.mutedUntil).toLocaleString("ru-RU", { timeZone: "Europe/Moscow" });
          return `├ 🔇 ${userM} — Причина: ${m.muteReason || "отсутствует"} | Выдал(а): ${adm} | Дата окончания: ${exp}`;
        }));
        itemsStr = lines.join("\n");
      }

      const text = 
`🔇 Список пользователей с мутом
👥 Всего пользователей: ${mutes.length}
📁 Список пользователей:
${itemsStr}`;

      const kb = inlineKeyboard([[cbBtn("Назад", { act: "list_tab", tab: "back", peer: peerId }, "negative")]]);
      await editBotMessage(peerId, cmId, text, kb);
      await showEventSnackbar(eventId, peerId, userId, "Муты");
      return;
    }

    if (payload.tab === "warns") {
      const warns = [];
      for (const [key, m] of memoryStore.members.entries()) {
        if (key.startsWith(`${peerId}_`) && m.warns > 0) {
          warns.push(m);
        }
      }

      let itemsStr = "└ Список пуст";
      if (warns.length > 0) {
        const lines = await Promise.all(warns.map(async m => {
          const userM = await getUserMention(m.userId);
          return `├ ⚠️ ${userM} — Активных предупреждений: ${m.warns}`;
        }));
        itemsStr = lines.join("\n");
      }

      const text = 
`⚠️ Список пользователей с предупреждениями
👥 Всего предупреждений: ${warns.reduce((a, b) => a + b.warns, 0)}
📁 Список предупреждений:
${itemsStr}`;

      const kb = inlineKeyboard([[cbBtn("Назад", { act: "list_tab", tab: "back", peer: peerId }, "negative")]]);
      await editBotMessage(peerId, cmId, text, kb);
      await showEventSnackbar(eventId, peerId, userId, "Варны");
      return;
    }

    if (payload.tab === "bans") {
      const bans = [];
      for (const [key, m] of memoryStore.members.entries()) {
        if (key.startsWith(`${peerId}_`) && m.bannedUntil && m.bannedUntil > Date.now()) {
          bans.push(m);
        }
      }

      let itemsStr = "└ Список пуст";
      if (bans.length > 0) {
        const lines = await Promise.all(bans.map(async m => {
          const userM = await getUserMention(m.userId);
          const adm = m.bannedBy ? await getUserMention(m.bannedBy) : "—";
          return `├ 🔒 ${userM} — Причина: ${m.banReason || "отсутствует"} | Выдал(а): ${adm} | Дата окончания: бессрочно`;
        }));
        itemsStr = lines.join("\n");
      }

      const text = 
`🔒 Список заблокированных пользователей
👥 Всего пользователей: ${bans.length}
📁 Список пользователей:
${itemsStr}`;

      const kb = inlineKeyboard([[cbBtn("Назад", { act: "list_tab", tab: "back", peer: peerId }, "negative")]]);
      await editBotMessage(peerId, cmId, text, kb);
      await showEventSnackbar(eventId, peerId, userId, "Баны");
      return;
    }
  }
}

// Показ всплывающего уведомления по клику на callback кнопку
async function showEventSnackbar(eventId, peerId, userId, text) {
  try {
    await callVkApi("messages.sendMessageEventAnswer", {
      event_id: eventId,
      user_id: userId,
      peer_id: peerId,
      event_data: JSON.stringify({
        type: "show_snackbar",
        text: text
      })
    });
  } catch {}
}
