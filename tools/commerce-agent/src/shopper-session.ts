export type McpGrant = "cart:read" | "cart:write" | "orders:read";
const grants = {
  "cart:read": { path: "mcp-token", scope: "cart:read" },
  "cart:write": { path: "mcp-cart-token", scope: "cart:read cart:write" },
  "orders:read": { path: "mcp-orders-token", scope: "orders:read" },
} as const;
// Cookie and CSRF values live only inside this local CLI object, never in graph state/configuration.
export class ShopperSession {
  private readonly cookies = new Map<string, string>();
  private signedInOwner?: string;
  private readonly tokens = new Map<McpGrant, { value: string; expires: number }>();
  constructor(private readonly request: typeof fetch = fetch) {}
  private async send(path: string, init: RequestInit = {}) {
    const response = await this.request(`http://127.0.0.1:5080/api/auth/${path}`, {
      ...init, redirect: "error", signal: AbortSignal.timeout(10_000),
      headers: { ...init.headers, Cookie: [...this.cookies].map(([key, value]) => `${key}=${value}`).join("; ") },
    });
    for (const cookie of response.headers.getSetCookie()) {
      const pair = cookie.split(";", 1)[0]!;
      const index = pair.indexOf("=");
      if (index > 0) this.cookies.set(pair.slice(0, index), pair.slice(index + 1));
    }
    if (!response.ok) throw new Error("Account request failed. Sign in again or check the local API.");
    return response;
  }
  private async csrf(): Promise<string> {
    const body = await (await this.send("csrf")).json() as { token?: unknown };
    if (typeof body.token !== "string" || !body.token) throw new Error("Could not obtain a CSRF token.");
    return body.token;
  }
  async login(email: string, password: string) {
    this.clear();
    if (!email.trim() || email.length > 254 || !password || password.length > 128) throw new Error("Enter a valid email and password.");
    try {
      await this.send("login", { method: "POST", headers: { "Content-Type": "application/json", "X-CSRF-Token": await this.csrf() },
        body: JSON.stringify({ email, password }) });
      await this.owner();
    } catch { this.clear(); throw new Error("Sign-in failed. Check your account details and the local API."); }
  }
  async owner(): Promise<string> {
    const body = await (await this.send("session")).json() as { user?: { id?: unknown } | null };
    const owner = body.user?.id;
    if (typeof owner !== "string" || !owner || (this.signedInOwner && this.signedInOwner !== owner)) {
      this.clear(); throw new Error("Your session ended or changed. Sign in again.");
    }
    this.signedInOwner = owner;
    return owner;
  }
  async issueMcpToken(grant: McpGrant): Promise<string> {
    if (!Object.hasOwn(grants, grant)) throw new Error("Unsupported MCP grant.");
    await this.owner();
    const cached = this.tokens.get(grant);
    if (cached && cached.expires > Date.now() + 185_000) return cached.value;
    const { path, scope } = grants[grant];
    const body = await (await this.send(path, { method: "POST", headers: { "X-CSRF-Token": await this.csrf() } })).json() as
      { accessToken?: unknown; scope?: unknown; expiresAt?: unknown };
    const expires = typeof body.expiresAt === "string" ? Date.parse(body.expiresAt) : NaN;
    if (typeof body.accessToken !== "string" || !body.accessToken || body.accessToken.length > 8192 || /\s/.test(body.accessToken) ||
        body.scope !== scope || !Number.isFinite(expires) || expires <= Date.now() || expires > Date.now() + 310_000) {
      throw new Error("The API returned an invalid MCP grant.");
    }
    this.tokens.set(grant, { value: body.accessToken, expires });
    return body.accessToken;
  }
  private clear() { this.cookies.clear(); this.tokens.clear(); this.signedInOwner = undefined; }
  async logout() {
    try {
      if (this.signedInOwner) await this.send("logout", { method: "POST", headers: { "X-CSRF-Token": await this.csrf() } });
    } finally { this.clear(); }
  }
}
