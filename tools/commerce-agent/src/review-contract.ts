import { z } from "zod";

export const reviewThread = (id: string) => `review:${id}`;
export const guid = z.string().regex(/^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i);
const kind = z.enum(["Checkout", "Cancellation", "Refund"]);
export const reviewIntent = z.discriminatedUnion("kind", [
  z.object({ kind: z.literal("Checkout"), paymentMode: z.enum(["success", "failure", "delay"]) }).strict(),
  z.object({ kind: z.literal("Cancellation"), orderId: guid }).strict(),
  z.object({ kind: z.literal("Refund"), orderId: guid }).strict(),
]);
export type ReviewIntent = z.infer<typeof reviewIntent>;
export const reviewSchema = z.object({ id: guid, kind,
  status: z.enum(["Pending", "Completed", "Rejected", "Expired", "Superseded", "Stale"]),
  snapshotHash: z.string().regex(/^[0-9A-Fa-f]{64}$/), createdAt: z.iso.datetime({ offset: true }),
  expiresAt: z.iso.datetime({ offset: true }), orderId: guid.nullish(),
  snapshot: z.object({ kind, total: z.number().finite().nonnegative(), currency: z.literal("USD"),
    paymentMode: z.enum(["success", "failure", "delay"]), destination: z.string().max(500),
    orderId: guid.nullish(), orderVersion: z.number().int().nullish(),
    items: z.array(z.object({ productId: guid, name: z.string().min(1).max(250), artwork: z.string().max(200), color: z.string().max(100),
      quantity: z.number().int().min(1).max(99), unitPrice: z.number().finite().nonnegative(), availableStock: z.number().int().nonnegative() })).min(1).max(100),
  }),
}).refine(value => value.kind === value.snapshot.kind, "Inconsistent review kind.");
export type Review = z.infer<typeof reviewSchema>;
export type ReviewMemory = { checkpointer: import("@langchain/langgraph").BaseCheckpointSaver; threadId: string };
export const pendingReview = (review: Review) => review.status === "Pending" && Date.parse(review.expiresAt) > Date.now();
export const sameReview = (a: Review, b: Review) => a.id === b.id && a.snapshotHash === b.snapshotHash
  && JSON.stringify(a.snapshot) === JSON.stringify(b.snapshot) && a.kind === b.kind
  && Date.parse(a.expiresAt) === Date.parse(b.expiresAt);

// Never abbreviate review terms or let a language model rewrite the approval summary.
export function formatReview(review: Review): string {
  const text = (value: string) => value.replace(/[\u0000-\u001f\u007f-\u009f\u202a-\u202e\u2066-\u2069]/g, " ");
  return ["--- HUMAN REVIEW (demo only) ---", `Action: ${review.kind} | Status: ${review.status}`,
    `Review ID: ${review.id}`, `Expires: ${review.expiresAt}`, `Snapshot: ${review.snapshotHash}`,
    ...review.snapshot.items.map(item => `${text(item.name)} [${item.productId}] | quantity ${item.quantity} | unit ${item.unitPrice.toFixed(2)} ${review.snapshot.currency} | stock ${item.availableStock}`),
    `Total: ${review.snapshot.total.toFixed(2)} ${review.snapshot.currency}`,
    `Destination: ${text(review.snapshot.destination)}`, `Demo payment outcome: ${review.snapshot.paymentMode}`,
    ...(review.snapshot.orderId ? [`Order: ${review.snapshot.orderId} | version ${review.snapshot.orderVersion}`] : []),
    ...(review.orderId ? [`Resulting order: ${review.orderId}`] : []),
    "Approval executes this exact review; payment/shipping are simulated. Completed does not mean payment succeeded.",
    ...(pendingReview(review) ? [`/approve ${review.id}`, `/reject ${review.id}`] : ["This review is no longer awaiting a decision."]),
  ].join("\n");
}
