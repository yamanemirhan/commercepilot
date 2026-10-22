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
