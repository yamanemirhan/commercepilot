"use client";

import {
  Check,
  Clock3,
  Package,
  ShoppingBag,
  AlertCircle,
  ArrowRight,
  Plus,
  Minus,
  Trash2,
} from "lucide-react";
import { Product, money } from "@/lib/catalog";
import { ProductArt } from "./product-art";
import Link from "next/link";

export type Card = {
  cardId: string;
  kind: string;
  schemaVersion: number;
  toolCallId: string;
  entityId: string | null;
  observedAt: string;
  payload: unknown;
};
type Line = {
  productId: string;
  name: string;
  artwork: string;
  color: string;
  quantity: number;
  unitPrice: number;
  availableStock: number;
};
type Cart = {
  items: Line[];
  total: number;
  currency: string;
  itemCount: number;
  isAvailable: boolean;
};
type Order = {
  id: string;
  number: string;
  status: string;
  paymentStatus: string;
  items: Line[];
  total: number;
  currency: string;
  capturedAmount: number;
  refundedAmount: number;
  destination: string;
  canCancel: boolean;
  canRefund: boolean;
  timeline: { at: string; title: string; detail: string }[];
};
export type Approval = {
  id: string;
  kind: string;
  status: string;
  expiresAt: string;
  snapshotHash: string;
  orderId: string | null;
  snapshot: {
    items: Line[];
    total: number;
    currency: string;
    destination: string;
    paymentMode: string;
    orderId: string | null;
  };
};
type Props = {
  card: Card;
  busy: boolean;
  now: number;
  send: (text: string) => void;
  decide: (card: Card, decision: "approve" | "reject") => void;
  navigate?: () => void;
};
const label = (value: string) => value.replace(/([a-z])([A-Z])/g, "$1 $2");

export function ChatCardView({ card, busy, now, send, decide, navigate }: Props) {
  if (card.schemaVersion !== 1)
    return <div className="chat-card">Refresh to view this updated card.</div>;
  if (card.kind === "knowledge") {
    const data = card.payload as {
      releaseId: string;
      sources: {
        documentId: string;
        title: string;
        excerpt: string;
        reviewedAt: string;
        links: { label: string; path: string }[];
      }[];
    };
    return (
      <section className="chat-card chat-sources">
        <h4>
          <Package size={16} />
          Site reference
        </h4>
        <p>Verified guidance retrieved for this reply.</p>
        {data.sources.map((source) => (
          <details key={source.documentId}>
            <summary>{source.title}</summary>
            <p className="knowledge-excerpt">{source.excerpt}</p>
            <div className="chat-actions">
              {source.links
                .filter(
                  (link) =>
                    link.path.startsWith("/") &&
                    !link.path.startsWith("//") &&
                    !/[\\%\u0000-\u001f]/.test(link.path),
                )
                .map((link) => (
                  <Link key={link.path} href={link.path} onClick={navigate}>
                    {link.label} <ArrowRight size={13} />
                  </Link>
                ))}
            </div>
            <small>
              Reviewed {new Date(source.reviewedAt).toLocaleDateString("en-US")}
            </small>
          </details>
        ))}
        <small>
          Reference version {data.releaseId.slice(0, 8)} · This snapshot may
          change in future replies.
        </small>
      </section>
    );
  }
  if (card.kind === "error") {
    const data = card.payload as { title: string; message: string };
    return (
      <section className="chat-card chat-error">
        <h4>
          <AlertCircle size={16} />
          {data.title}
        </h4>
        <p>{data.message}</p>
        <small>
          No success is assumed. Ask to check the current state before repeating
          a change.
        </small>
      </section>
    );
  }
  if (card.kind === "products" || card.kind === "product") {
    const data = card.payload as { items: Product[]; total: number };
    const products =
      card.kind === "product" ? [card.payload as Product] : data.items;
    return (
      <section className="chat-card">
        <h4>
          <Package size={16} />
          {card.kind === "product"
            ? "A closer look"
            : `${products.length} of ${data.total} matching essentials`}
        </h4>
        {products.length === 0 && (
          <p>
            No products match those filters. Try another category or a wider
            budget.
          </p>
        )}
        <ol className="chat-products">
          {products.map((p, index) => (
            <li key={p.id}>
              <div className={`chat-art ${p.color}`}>
                <ProductArt kind={p.artwork} />
              </div>
              <div className="chat-product-copy">
                <span className="chat-kicker">
                  {card.kind === "products" ? `${index + 1} / ` : ""}
                  {p.category}
                </span>
                <strong>{p.name}</strong>
                <span>
                  {money(p.price, p.currency)}{" "}
                  <small>
                    · {p.stock > 0 ? `${p.stock} in stock` : "Out of stock"}
                  </small>
                </span>
                {card.kind === "product" && <p>{p.description}</p>}
                <div className="chat-card-actions">
                  {card.kind === "products" && (
                    <button
                      disabled={busy}
                      onClick={() => send(`Show the details of ${p.name}.`)}
                    >
                      Details
                    </button>
                  )}
                  <button
                    disabled={busy || p.stock < 1}
                    onClick={() => send(`Add one ${p.name} to my bag.`)}
                  >
                    <Plus size={12} />
                    Add to bag
                  </button>
                </div>
              </div>
            </li>
          ))}
        </ol>
        <small className="chat-card-foot">
          Prices and availability are checked again when you act.
        </small>
      </section>
    );
  }
  if (card.kind === "cart") {
    const cart = card.payload as Cart;
    return (
      <section className="chat-card">
        <h4>
          <ShoppingBag size={16} />
          Your bag <span className="chat-badge">{cart.itemCount} items</span>
        </h4>
        {!cart.items.length && (
          <p>
            A little room for something good. Ask me to find your next favorite.
          </p>
        )}
        <Lines items={cart.items} currency={cart.currency} />
        {cart.items.map((item) => (
          <div className="chat-quantity-row" key={item.productId}>
            <span>{item.name}</span>
            <div className="chat-card-actions">
              <button
                aria-label={`Decrease ${item.name}`}
                disabled={busy || item.quantity <= 1}
                onClick={() =>
                  send(
                    `Read my current bag, then decrease ${item.name} by one unit.`,
                  )
                }
              >
                <Minus size={12} />
              </button>
              <span>{item.quantity}</span>
              <button
                aria-label={`Increase ${item.name}`}
                disabled={
                  busy || item.quantity >= Math.min(99, item.availableStock)
                }
                onClick={() =>
                  send(
                    `Read my current bag, then increase ${item.name} by one unit.`,
                  )
                }
              >
                <Plus size={12} />
              </button>
              <button
                aria-label={`Remove ${item.name}`}
                disabled={busy}
                onClick={() => send(`Remove ${item.name} from my bag.`)}
              >
                <Trash2 size={13} />
              </button>
            </div>
          </div>
        ))}
        <div className="chat-total">
          <span>Bag total</span>
          <strong>{money(cart.total, cart.currency)}</strong>
        </div>
        {!!cart.items.length && (
          <button
            className="chat-primary"
            disabled={busy || !cart.isAvailable}
            onClick={() => send("Prepare checkout for my bag.")}
          >
            Review checkout <ArrowRight size={14} />
          </button>
        )}
        <small className="chat-card-foot">
          Adding to your bag does not reserve stock.
        </small>
      </section>
    );
  }
  if (card.kind === "orders") {
    const data = card.payload as { orders: Order[]; limit: number };
    return (
      <section className="chat-card">
        <h4>
          <Package size={16} />
          Your orders
        </h4>
        {!data.orders.length && (
          <p>No orders yet. Find something you love to get started.</p>
        )}
        <div className="chat-order-list">
          {data.orders.map((order) => (
            <button
              disabled={busy}
              key={order.id}
              onClick={() =>
                send(`Show the details and status of order ${order.number}.`)
              }
            >
              <span>
                <strong>{order.number}</strong>
                <small>
                  {label(order.status)} · Payment {label(order.paymentStatus)}
                </small>
              </span>
              <b>{money(order.total, order.currency)}</b>
              <ArrowRight size={14} />
            </button>
          ))}
        </div>
      </section>
    );
  }
  if (card.kind === "order") {
    const order = card.payload as Order;
    return (
      <section className="chat-card">
        <h4>
          <Package size={16} />
          {order.number}
          <span className="chat-badge">{label(order.status)}</span>
        </h4>
        <Lines items={order.items} currency={order.currency} />
        <div className="chat-total">
          <span>Total</span>
          <strong>{money(order.total, order.currency)}</strong>
        </div>
        <dl className="chat-facts">
          <div>
            <dt>Payment</dt>
            <dd>{label(order.paymentStatus)}</dd>
          </div>
          <div>
            <dt>Captured</dt>
            <dd>{money(order.capturedAmount, order.currency)}</dd>
          </div>
          <div>
            <dt>Refunded</dt>
            <dd>{money(order.refundedAmount, order.currency)}</dd>
          </div>
        </dl>
        <p className="chat-muted">{order.destination}</p>
        <ol className="chat-timeline">
          {order.timeline.map((event, i) => (
            <li key={i}>
              <Check size={12} />
              <div>
                <strong>{event.title}</strong>
                <small>{new Date(event.at).toLocaleString()}</small>
                <p>{event.detail}</p>
              </div>
            </li>
          ))}
        </ol>
        <div className="chat-card-actions">
          {order.canCancel && (
            <button
              disabled={busy}
              onClick={() =>
                send(`Prepare a cancellation review for order ${order.number}.`)
              }
            >
              Review cancellation
            </button>
          )}
          {order.canRefund && (
            <button
              disabled={busy}
              onClick={() =>
                send(`Prepare a full refund review for order ${order.number}.`)
              }
            >
              Review refund
            </button>
          )}
          <button
            disabled={busy}
            onClick={() =>
              send(`Check the latest status of order ${order.number}.`)
            }
          >
            Check status
          </button>
        </div>
      </section>
    );
  }
  if (card.kind === "approval") {
    const review = card.payload as Approval;
    const remaining = Math.max(
      0,
      Math.ceil((new Date(review.expiresAt).getTime() - now) / 1000),
    );
    const status =
      review.status === "Pending" && !remaining ? "Expired" : review.status;
    const valid = status === "Pending";
    return (
      <section
        className={`chat-card chat-approval ${valid ? "is-pending" : ""}`}
        aria-label={`${review.kind} review`}
      >
        <div className="chat-kicker">YOUR DECISION</div>
        <h4>
          {review.kind === "Checkout"
            ? "Ready when you are."
            : `Review ${review.kind.toLowerCase()}`}
          <span className="chat-badge">{status}</span>
        </h4>
        <p>
          {review.kind === "Checkout"
            ? "Review these exact items before placing your demo order."
            : `This prepares a ${review.kind.toLowerCase()} only. The change needs your approval.`}
        </p>
        <Lines
          items={review.snapshot.items}
          currency={review.snapshot.currency}
        />
        <div className="chat-total">
          <span>{review.kind === "Refund" ? "Refund total" : "Total"}</span>
          <strong>
            {money(review.snapshot.total, review.snapshot.currency)}
          </strong>
        </div>
        <p className="chat-muted">{review.snapshot.destination}</p>
        {review.kind === "Checkout" && (
          <p className="chat-muted">
            Demo payment: {review.snapshot.paymentMode}. No real charges.
          </p>
        )}
        {valid ? (
          <>
            <p className="chat-expiry">
              <Clock3 size={13} />
              Expires in {Math.floor(remaining / 60)}:
              {String(remaining % 60).padStart(2, "0")}
            </p>
            <div className="chat-decision">
              <button
                className="chat-secondary"
                disabled={busy}
                onClick={() => decide(card, "reject")}
              >
                Reject
              </button>
              <button
                className="chat-primary"
                disabled={busy}
                onClick={() => decide(card, "approve")}
              >
                <Check size={14} />
                Approve{" "}
                {review.kind === "Checkout"
                  ? money(review.snapshot.total, review.snapshot.currency) +
                    " order"
                  : review.kind.toLowerCase()}
              </button>
            </div>
            <small className="chat-card-foot">
              Only these buttons approve or reject. Typing “yes” does not.
            </small>
          </>
        ) : (
          <p className="chat-review-result">
            <Check size={14} />
            {status === "Completed"
              ? "Review completed. Check the order card for the separate payment status."
              : `This review is ${status.toLowerCase()}. Ask for a new review if needed.`}
          </p>
        )}
      </section>
    );
  }
  return null;
}

function Lines({ items, currency }: { items: Line[]; currency: string }) {
  return (
    <ul className="chat-lines">
      {items.map((item) => (
        <li key={item.productId}>
          <div className={`chat-line-art ${item.color}`}>
            <ProductArt kind={item.artwork} />
          </div>
          <div>
            <strong>{item.name}</strong>
            <small>
              {item.quantity} × {money(item.unitPrice, currency)} ·{" "}
              {item.availableStock} available
            </small>
          </div>
          <b>{money(item.quantity * item.unitPrice, currency)}</b>
        </li>
      ))}
    </ul>
  );
}
