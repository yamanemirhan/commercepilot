import { connectMcp } from "./mcp.js";
import { reviewIntent, reviewSchema } from "./review-contract.js";
import type { ReviewIntent } from "./review-contract.js";
import type { ShopperSession, McpGrant } from "./shopper-session.js";

export function reviewGateway(session: ShopperSession) {
  return {
    async prepare(input: ReviewIntent, signal: AbortSignal) {
      const intent = reviewIntent.parse(input);
      const grant: McpGrant = intent.kind === "Checkout" ? "checkout:prepare" : intent.kind === "Cancellation" ? "cancellation:prepare" : "refund:prepare";
      const name = intent.kind === "Checkout" ? "prepare_order" : intent.kind === "Cancellation" ? "prepare_cancellation" : "prepare_refund";
      const client = await connectMcp(signal, await session.issueMcpToken(grant));
      try {
        const tools = await client.listTools({}, { signal });
        if (!tools.tools.some(tool => tool.name === name)) throw new Error("Unavailable preparation tool.");
        const result = await client.callTool({ name, arguments: intent.kind === "Checkout" ? { paymentMode: intent.paymentMode } : { orderId: intent.orderId } }, { signal });
        if (result.isError) throw new Error("Preparation was not confirmed.");
        return reviewSchema.parse(result.structuredContent);
      } catch { throw new Error("Review preparation failed; do not retry automatically. Inspect pending reviews in the storefront."); }
      finally { await client.close(); }
    },
    read: (id: string, signal: AbortSignal) => session.readReview(id, signal),
    decide: (...args: Parameters<ShopperSession["decideReview"]>) => session.decideReview(...args),
  };
}
