import { NextRequest, NextResponse } from "next/server";

export async function GET(
  request: NextRequest,
  context: { params: Promise<{ path: string[] }> },
) {
  const { path } = await context.params;
  const isList =
    path.length === 1 && ["products", "categories"].includes(path[0]);
  const isProduct =
    path.length === 2 &&
    path[0] === "products" &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(
      path[1],
    );
  if (!isList && !isProduct)
    return NextResponse.json({ title: "Not found" }, { status: 404 });
  const base = process.env.API_BASE_URL ?? "http://127.0.0.1:5080";
  try {
    const response = await fetch(
      `${base}/api/${path.join("/")}${request.nextUrl.search}`,
      {
        cache: "no-store",
        signal: AbortSignal.timeout(8000),
      },
    );
    return new NextResponse(await response.text(), {
      status: response.status,
      headers: {
        "Content-Type":
          response.headers.get("Content-Type") ?? "application/json",
        "Cache-Control": "no-store",
      },
    });
  } catch {
    return NextResponse.json(
      { title: "The catalog is temporarily unavailable. Please try again." },
      { status: 503 },
    );
  }
}
