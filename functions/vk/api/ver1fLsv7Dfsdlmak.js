export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    // Проверяем путь для VK Callback API
    if (url.pathname === "/vk/api/ver1fLsv7Dfsdlmak") {
      if (request.method === "POST") {
        try {
          const body = await request.json();

          // 1. Подтверждение адреса сервера Callback API ВКонтакте
          if (body.type === "confirmation") {
            // Возвращаем строку подтверждения для группы 232664735
            return new Response("ce6d834c", {
              headers: { "Content-Type": "text/plain; charset=utf-8" },
            });
          }

          // 2. Если развернут основной бекенд (например на Render/VPS), можно проксировать запросы
          if (env.BACKEND_URL) {
            return await fetch(`${env.BACKEND_URL}${url.pathname}`, {
              method: "POST",
              headers: request.headers,
              body: JSON.stringify(body)
            });
          }

          // По спецификации VK Callback API всегда возвращаем "ok"
          return new Response("ok", {
            headers: { "Content-Type": "text/plain; charset=utf-8" },
          });
        } catch (err) {
          return new Response("ok", {
            headers: { "Content-Type": "text/plain; charset=utf-8" },
          });
        }
      }

      // Для GET-проверки в браузере
      return new Response("Mint VK Callback Endpoint is Active", { status: 200 });
    }

    return new Response("Not Found", { status: 404 });
  },
};
