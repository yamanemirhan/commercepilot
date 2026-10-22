// Cookie and CSRF values live only inside this local CLI object, never in graph state/configuration.
export class ShopperSession {
  private readonly cookies = new Map<string, string>();
  private signedInOwner?: string;
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
  private clear() { this.cookies.clear(); this.signedInOwner = undefined; }
  async logout() {
    try {
      if (this.signedInOwner) await this.send("logout", { method: "POST", headers: { "X-CSRF-Token": await this.csrf() } });
    } finally { this.clear(); }
  }
}
