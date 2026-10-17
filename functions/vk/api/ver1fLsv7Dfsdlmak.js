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

  // GET или любой другой запрос
  return new Response("ce6d834c", {
    status: 200,
    headers: {
      "Content-Type": "text/plain; charset=utf-8"
    }
  });
}
