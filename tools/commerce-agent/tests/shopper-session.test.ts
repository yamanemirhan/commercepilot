import assert from "node:assert/strict";
import { test } from "node:test";
import { ShopperSession } from "../src/shopper-session.js";

test("login uses CSRF and cookies, identity is revalidated and an expired/changed session is refused", async () => {
  let owner: string | null = "server-verified-alex";
  let checks = 0;
  const session = new ShopperSession(async (input, init) => {
    const path = String(input).split("/").at(-1);
    assert.equal(init?.redirect, "error");
    const headers = new Headers(init?.headers);
    if (path === "csrf") return Response.json({ token: "csrf-token" }, { headers: { "Set-Cookie": "csrf=cookie; Path=/; HttpOnly" } });
    if (path === "login") {
      assert.equal(headers.get("X-CSRF-Token"), "csrf-token");
      assert.equal(headers.get("Cookie"), "csrf=cookie");
      assert.equal(JSON.parse(String(init?.body)).password, "PASSWORD_MARKER");
      return Response.json({}, { headers: { "Set-Cookie": "identity=secret-cookie; Path=/; HttpOnly" } });
    }
    assert.equal(path, "session");
    assert.match(headers.get("Cookie")!, /identity=secret-cookie/);
    checks++;
    return Response.json({ user: owner ? { id: owner } : null });
  });
  await session.login("alex@example.test", "PASSWORD_MARKER");
  assert.equal(await session.owner(), "server-verified-alex");
  assert.equal(checks, 2);
  owner = "sam";
  await assert.rejects(session.owner(), /session ended or changed/);
});

test("authentication errors do not expose response bodies or submitted credentials", async () => {
  const session = new ShopperSession(async () => new Response("PASSWORD_MARKER SERVER_SECRET", { status: 401 }));
  await assert.rejects(session.login("alex@example.test", "PASSWORD_MARKER"), error => {
    assert.ok(error instanceof Error);
    assert.ok(!error.message.includes("MARKER"));
    return true;
  });
});

test("scoped grant issuance uses CSRF, validates scopes/expiry and caches only in the session", async () => {
  let grantCalls = 0, checks = 0;
  const session = new ShopperSession(async (input, init) => {
    const path = String(input).split("/").at(-1);
    if (path === "session") { checks++; return Response.json({ user: { id: "alex" } }); }
    if (path === "csrf") return Response.json({ token: "csrf" });
    assert.equal(path, "mcp-cart-token");
    assert.equal(init?.method, "POST");
    assert.equal(new Headers(init?.headers).get("X-CSRF-Token"), "csrf");
    grantCalls++;
    return Response.json({ accessToken: "PRIVATE_TOKEN", scope: "cart:read cart:write", expiresAt: new Date(Date.now() + 300_000).toISOString() });
  });
  assert.equal(await session.issueMcpToken("cart:write"), "PRIVATE_TOKEN");
  assert.equal(await session.issueMcpToken("cart:write"), "PRIVATE_TOKEN");
  assert.equal(grantCalls, 1); assert.equal(checks, 2);
});

for (const bad of [{ scope: "cart:read cart:write" }, { expiresAt: "2000-01-01T00:00:00Z" }, { accessToken: "bad token" }]) {
  test(`rejects malformed or overly broad grants: ${Object.keys(bad)[0]}`, async () => {
    const session = new ShopperSession(async input => {
      const path = String(input).split("/").at(-1);
      return Response.json(path === "session" ? { user: { id: "alex" } } : path === "csrf" ? { token: "csrf" }
        : { accessToken: "PRIVATE_TOKEN", scope: "cart:read", expiresAt: new Date(Date.now() + 300_000).toISOString(), ...bad });
    });
    await assert.rejects(session.issueMcpToken("cart:read"), /invalid MCP grant/);
  });
}
