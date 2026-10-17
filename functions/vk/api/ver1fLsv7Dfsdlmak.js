export async function onRequest(context) {
  const { request } = context;

  if (request.method === "POST") {
    try {
      const text = await request.text();
      // Если это подтверждение VK
      if (text.includes('"confirmation"') || text.includes('confirmation')) {
        return new Response("ce6d834c", {
          status: 200,
          headers: {
            "Content-Type": "text/plain; charset=utf-8",
            "Access-Control-Allow-Origin": "*"
          }
        });
      }

      // Если настроен BACKEND_URL в переменных окружения Cloudflare Pages,
      // проксируем входящие события Callback API на запущенный бекенд
      if (context.env && context.env.BACKEND_URL) {
        try {
          const backendEndpoint = `${context.env.BACKEND_URL}/vk/api/ver1fLsv7Dfsdlmak`;
          await fetch(backendEndpoint, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: text
          });
        } catch (err) {
          // Игнорируем ошибки проксирования, чтобы всегда вернуть ok ВКонтакте
        }
      }

      return new Response("ok", {
        status: 200,
        headers: { "Content-Type": "text/plain; charset=utf-8" }
      });
    } catch {
      return new Response("ce6d834c", {
        status: 200,
        headers: { "Content-Type": "text/plain; charset=utf-8" }
      });
    }
  }

  return new Response("ce6d834c", {
    status: 200,
    headers: { "Content-Type": "text/plain; charset=utf-8" }
  });
}
