"use client";

import Link from "next/link";
import { useCallback, useEffect, useRef, useState } from "react";
import {
  ArrowLeft,
  ArrowUpRight,
  Check,
  Layers3,
  LoaderCircle,
  Package,
  RefreshCw,
  Sparkles,
} from "lucide-react";
import { request, ShoppingControls, useShopping } from "./shopping";
import { ProductArt } from "./product-art";
import { money } from "@/lib/catalog";
import "./order-page.css";

type Order = {
  id: string;
  number: string;
  status: string;
  paymentStatus: string;
  total: number;
  currency: string;
  capturedAmount: number;
  refundedAmount: number;
  createdAt: string;
  destination: string;
  canCancel: boolean;
  canRefund: boolean;
  items: {
    productId: string;
    name: string;
    artwork: string;
    color: string;
    quantity: number;
    unitPrice: number;
  }[];
  timeline: { at: string; title: string; detail: string }[];
};
const label = (s: string) => s.replace(/([a-z])([A-Z])/g, "$1 $2");
const date = (s: string) =>
  new Date(s).toLocaleString("en-US", {
    dateStyle: "medium",
    timeStyle: "short",
  });

export function OrderPage({ orderId }: { orderId?: string }) {
  const shop = useShopping();
  // A changed owner gets a completely fresh view; old responses cannot repopulate it.
  return (
    <div className="orders-page">
      <header className="orders-header shell">
        <Link href="/" className="brand">
          <span className="brand-mark">
            <Layers3 size={19} />
          </span>
          commercepilot°
        </Link>
        <ShoppingControls />
      </header>
      <main className="shell orders-main">
        <Link href={orderId ? "/orders" : "/"} className="orders-back">
          <ArrowLeft size={15} />
          {orderId ? "All orders" : "Back to the collection"}
        </Link>
        {!shop.ready && !shop.user ? (
          <p className="orders-state">
            <LoaderCircle className="spin" size={20} />
            Checking your session…
          </p>
        ) : !shop.user ? (
          <section className="orders-empty">
            <Package size={38} strokeWidth={1.2} />
            <h1>Your orders, all together.</h1>
            <p>
              Sign in to see your purchases, payment details and delivery
              updates.
            </p>
            <button
              className="button dark"
              onClick={() => shop.setAccountOpen(true)}
            >
              Sign in
            </button>
          </section>
        ) : (
          <OwnedOrders
            key={`${shop.user.id}:${orderId ?? "list"}`}
            orderId={orderId}
          />
        )}
      </main>
      <footer className="shell orders-footer">
        A fictional collection. Demo payments and delivery. No real charges.
      </footer>
    </div>
  );
}

function OwnedOrders({ orderId }: { orderId?: string }) {
  const shop = useShopping();
  const [orders, setOrders] = useState<Order[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const sequence = useRef(0);
  const load = useCallback(async () => {
    const current = ++sequence.current;
    try {
      const data = orderId
        ? [await request<Order>(`orders/${orderId}`)]
        : await request<Order[]>("orders");
      if (sequence.current === current) {
        setOrders(data);
        setError(null);
      }
    } catch (e) {
      if (sequence.current === current) {
        setOrders([]);
        setError((e as Error).message);
      }
    } finally {
      if (sequence.current === current) setLoading(false);
    }
  }, [orderId]);
  useEffect(() => {
    void load();
    const timer = setInterval(() => {
      if (document.visibilityState === "visible") void load();
    }, 15000);
    return () => {
      sequence.current++;
      clearInterval(timer);
    };
  }, [load]);
  if (loading)
    return (
      <p className="orders-state" role="status">
        <LoaderCircle className="spin" size={20} />
        Finding your orders…
      </p>
    );
  if (error)
    return (
      <section className="orders-empty" role="alert">
        <Package size={35} />
        <h1>We couldn’t open your orders.</h1>
        <p>{error}</p>
        <button
          className="button light"
          onClick={() => {
            setLoading(true);
            void load();
          }}
        >
          Try again
        </button>
      </section>
    );
  if (!orders.length)
    return (
      <section className="orders-empty">
        <Package size={40} strokeWidth={1.2} />
        <div className="eyebrow">YOUR NEXT GOOD FIND</div>
        <h1>A little room for something good.</h1>
        <p>Your orders will appear here after you approve a checkout review.</p>
        <Link className="button dark" href="/#collection">
          Explore the collection <ArrowUpRight size={16} />
        </Link>
      </section>
    );
  const order = orders[0];
  return (
    <>
      <div className="orders-title">
        <div>
          <div className="eyebrow">YOUR EVERYDAY, ALL IN ONE PLACE</div>
          <h1>{orderId ? order.number : "Your orders."}</h1>
          <p>
            {orderId
              ? `Placed ${date(order.createdAt)}`
              : "Good finds. Every step of the way."}
          </p>
        </div>
        <button className="button light" onClick={() => void load()}>
          <RefreshCw size={15} />
          Refresh
        </button>
      </div>
      {!orderId ? (
        <div className="orders-grid">
          {orders.map((o) => (
            <Link href={`/orders/${o.id}`} className="order-summary" key={o.id}>
              <div className="order-summary-top">
                <span>{o.number}</span>
                <span className="order-status">{label(o.status)}</span>
              </div>
              <div className="order-preview">
                {o.items.slice(0, 3).map((item) => (
                  <div
                    className={`order-art ${item.color}`}
                    key={item.productId}
                  >
                    <ProductArt kind={item.artwork} />
                  </div>
                ))}
              </div>
              <h2>{o.items.map((i) => i.name).join(", ")}</h2>
              <p>{date(o.createdAt)}</p>
              <div className="order-summary-bottom">
                <span>Payment · {label(o.paymentStatus)}</span>
                <strong>{money(o.total, o.currency)}</strong>
                <ArrowUpRight size={18} />
              </div>
            </Link>
          ))}
        </div>
      ) : (
        <div className="order-detail-grid">
          <div>
            <section className="order-surface">
              <div className="order-section-heading">
                <h2>The good things inside</h2>
                <span className="order-status">{label(order.status)}</span>
              </div>
              {order.items.map((item) => (
                <div className="order-detail-line" key={item.productId}>
                  <div className={`order-art ${item.color}`}>
                    <ProductArt kind={item.artwork} />
                  </div>
                  <div>
                    <h3>{item.name}</h3>
                    <p>
                      {item.quantity} × {money(item.unitPrice, order.currency)}
                    </p>
                  </div>
                  <strong>
                    {money(item.quantity * item.unitPrice, order.currency)}
                  </strong>
                </div>
              ))}
              <div className="order-total">
                <span>Order total</span>
                <strong>{money(order.total, order.currency)}</strong>
              </div>
            </section>
            <section className="order-surface">
              <h2>Every step, recorded</h2>
              <ol className="order-events">
                {order.timeline.map((event, index) => (
                  <li key={index}>
                    <Check size={14} />
                    <div>
                      <h3>{event.title}</h3>
                      <time>{date(event.at)}</time>
                      <p>{event.detail}</p>
                    </div>
                  </li>
                ))}
              </ol>
            </section>
          </div>
          <aside>
            <section className="order-surface">
              <h2>Payment & delivery</h2>
              <dl className="order-facts">
                <div>
                  <dt>Payment status</dt>
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
              <p className="order-destination">{order.destination}</p>
              <small>Demo payment and delivery. No real charges.</small>
            </section>
            <section className="order-surface order-help">
              <Sparkles size={23} />
              <h2>A little help with this order?</h2>
              <p>
                Review changes with CommercePilot. You’ll see the exact terms
                and approve or reject inside chat.
              </p>
              {order.canCancel && (
                <button
                  className="button dark"
                  onClick={() =>
                    shop.openChat(
                      `Prepare a cancellation review for order ${order.number}.`,
                    )
                  }
                >
                  Review cancellation
                </button>
              )}
              {order.canRefund && (
                <button
                  className="button dark"
                  onClick={() =>
                    shop.openChat(
                      `Prepare a full refund review for order ${order.number}.`,
                    )
                  }
                >
                  Review refund
                </button>
              )}
              <button
                className="button light"
                onClick={() =>
                  shop.openChat(`Tell me about order ${order.number}.`)
                }
              >
                Ask about this order <ArrowUpRight size={15} />
              </button>
            </section>
          </aside>
        </div>
      )}
      <p className="orders-footnote">
        Updates refresh every 15 seconds while this page is visible. Your most
        recent 100 orders are available here.
      </p>
    </>
  );
}
