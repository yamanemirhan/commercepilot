import { z } from "zod";

export const catalogNames = ["search_products", "get_product"];
export const shoppingReadNames = ["get_cart", "get_my_orders", "get_order_status"];
export const cartWriteNames = ["add_to_cart", "update_cart_item", "remove_from_cart"];
export type ShoppingAccess = { cartWrite: boolean };
export const permittedNames = (access?: ShoppingAccess) => [...catalogNames,
  ...(access ? shoppingReadNames : []), ...(access?.cartWrite ? cartWriteNames : [])];
const text = z.string().max(250);
const amount = z.number().finite();
// .NET Guid permits the deterministic version-0 UUIDs used by this catalog seed.
const guid = z.string().regex(/^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i);
const cartItem = z.object({ productId: guid, name: text, quantity: z.number().int(),
  unitPrice: amount, availableStock: z.number().int(), lineTotal: amount });
const orderSummary = z.object({ id: guid, number: text, status: text, paymentStatus: text,
  total: amount, currency: text, createdAt: text });

// Only useful commerce fields reach the local model/checkpoint. Drop addresses, owner IDs and transport metadata.
export function shoppingResult(name: string, data: unknown): unknown {
  if (name === "get_cart" || cartWriteNames.includes(name)) {
    const cart = z.object({ items: z.array(cartItem), itemCount: z.number().int(), total: amount, currency: text }).parse(data);
    return { ...cart, items: cart.items.slice(0, 10), hasMoreItems: cart.items.length > 10 };
  }
  if (name === "get_my_orders") {
    const result = z.object({ orders: z.array(orderSummary), limit: z.number().int() }).parse(data);
    return { orders: result.orders.slice(0, 5), hasMoreOrders: result.orders.length > 5, sourceLimit: result.limit };
  }
  if (name === "get_order_status") {
    const order = orderSummary.extend({ capturedAmount: amount, refundedAmount: amount,
      canCancel: z.boolean(), canRefund: z.boolean(),
      items: z.array(z.object({ productId: guid, name: text, quantity: z.number().int(), unitPrice: amount })),
      timeline: z.array(z.object({ at: text, title: text, detail: text })) }).parse(data);
    return { ...order, items: order.items.slice(0, 10), hasMoreItems: order.items.length > 10,
      timeline: order.timeline.slice(-5), hasEarlierEvents: order.timeline.length > 5 };
  }
  return data;
}
