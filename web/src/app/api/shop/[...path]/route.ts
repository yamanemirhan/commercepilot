import { NextRequest, NextResponse } from "next/server";

const methods: Record<string, string[]> = {
  "auth/session": ["GET"],
  "auth/csrf": ["GET"],
  "auth/login": ["POST"],
  "auth/logout": ["POST"],
  cart: ["GET"],
  "cart/items": ["POST"],
  orders: ["GET"],
  actions: ["GET"],
  "orders/prepare": ["POST"],
};

async function forward(
  request: NextRequest,
  context: { params: Promise<{ path: string[] }> },
) {
  const { path } = await context.params;
  const route = path.join("/");
  const itemRoute =
    /^cart\/items\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(
      route,
    );
  const guid = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";
  const orderRead = new RegExp(`^(orders|actions)/${guid}$`, "i").test(route);
  const orderWrite = new RegExp(
    `^(orders/${guid}/prepare-(cancellation|refund)|actions/${guid}/(confirm|reject))$`,
    "i",
  ).test(route);
  const allowed = itemRoute
    ? ["PUT", "DELETE"]
    : orderRead
      ? ["GET"]
      : orderWrite
        ? ["POST"]
        : methods[route];
  if (!allowed)
    return NextResponse.json({ detail: "Not found." }, { status: 404 });
  if (!allowed.includes(request.method))
    return NextResponse.json(
      { detail: "Method not allowed." },
      { status: 405, headers: { Allow: allowed.join(", ") } },
    );

  const headers = new Headers({ Accept: "application/json" });
  // Forward only application cookies. No arbitrary caller authorization or upstream headers.
  const cookies = request.cookies
    .getAll()
    .filter((cookie) =>
      /^(commercepilot\.(session|csrf))(C\d+)?$/.test(cookie.name),
    );
  headers.set(
    "Cookie",
    cookies.map((cookie) => `${cookie.name}=${cookie.value}`).join("; "),
  );
  let body: string | undefined;
  if (request.method !== "GET") {
    const origin = process.env.APP_ORIGIN ?? "http://localhost:3000";
    if (request.headers.get("origin") !== origin)
      return NextResponse.json(
        { detail: "This request must come from the storefront." },
        { status: 403 },
      );
    const csrf = request.headers.get("X-CSRF-Token");
    if (!csrf)
      return NextResponse.json(
        { detail: "Refresh the page and try again." },
        { status: 400 },
      );
    headers.set("X-CSRF-Token", csrf);
    if (request.method !== "DELETE") {
      if (
        request.headers.get("content-type")?.split(";")[0] !==
        "application/json"
      )
        return NextResponse.json(
          { detail: "JSON is required." },
          { status: 415 },
        );
      body = await request.text();
      if (body.length > 4096)
        return NextResponse.json(
          { detail: "Request is too large." },
          { status: 413 },
        );
      headers.set("Content-Type", "application/json");
    }
  }

  try {
    const base = process.env.API_BASE_URL ?? "http://127.0.0.1:5080";
    const upstream = await fetch(`${base}/api/${route}`, {
      method: request.method,
      headers,
      body,
      cache: "no-store",
      redirect: "manual",
      signal: AbortSignal.timeout(10000),
    });
    const result = new NextResponse(
      upstream.status === 204 ? null : await upstream.text(),
      {
        status: upstream.status,
        headers: {
          "Content-Type":
            upstream.headers.get("Content-Type") ?? "application/json",
          "Cache-Control": "no-store",
        },
      },
    );
    for (const cookie of upstream.headers.getSetCookie())
      result.headers.append("Set-Cookie", cookie);
    return result;
  } catch {
    // Never retry a mutation automatically: the upstream may have committed before a timeout.
    return NextResponse.json(
      {
        detail:
          "We couldn’t reach the store. Refresh to check the result before trying again. Repeating the same approval will not place a second order.",
      },
      { status: 503 },
    );
  }
}

export { forward as GET, forward as POST, forward as PUT, forward as DELETE };
