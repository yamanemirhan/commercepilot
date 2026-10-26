import { reviewSchema } from "../src/review-contract.js";
export function reviewFixture() {
  return reviewSchema.parse({ id: "22222222-2222-4222-8222-222222222222", kind: "Checkout", status: "Pending",
    snapshotHash: "A".repeat(64), createdAt: new Date().toISOString(), expiresAt: new Date(Date.now() + 300_000).toISOString(), orderId: null,
    snapshot: { kind: "Checkout", total: 20, currency: "USD", paymentMode: "success", destination: "Fictional demo destination",
      orderId: null, orderVersion: null, items: [{ productId: "10000000-0000-0000-0000-000000000020", name: "Test speaker",
        artwork: "speaker", color: "#ffffff", quantity: 2, unitPrice: 10, availableStock: 10 }] } });
}
