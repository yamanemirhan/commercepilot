"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import {
  ArrowLeft,
  ArrowRight,
  Check,
  Clock3,
  LoaderCircle,
  Package,
  RefreshCw,
  ShieldCheck,
  Truck,
  X,
} from "lucide-react";
import { money } from "@/lib/catalog";
import { ProductArt } from "./product-art";
import { request, useShopping } from "./shopping";

type Line = {
  productId: string;
  name: string;
  artwork: string;
  color: string;
  quantity: number;
  unitPrice: number;
};
type Approval = {
  id: string;
  kind: string;
  status: string;
  snapshotHash: string;
  expiresAt: string;
  orderId: string | null;
  snapshot: {
    items: Line[];
    total: number;
    currency: string;
    paymentMode: string;
    destination: string;
    orderId: string | null;
  };
};
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
  nextTransitionAt: string | null;
  destination: string;
  items: Line[];
  canCancel: boolean;
  canRefund: boolean;
  timeline: { at: string; title: string; detail: string }[];
};
const label = (value: string) => value.replace(/([a-z])([A-Z])/g, "$1 $2");
const date = (value: string) =>
  new Date(value).toLocaleString("en-US", {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
  });

export function OrderCenter({
  open,
  checkout,
  onClose,
}: {
  open: boolean;
  checkout: boolean;
  onClose: () => void;
}) {
  const shop = useShopping();
  const dialog = useRef<HTMLDialogElement>(null);
  const mounted = useRef(true);
  const pending = useRef(false);
  const loadVersion = useRef(0);
  const [orders, setOrders] = useState<Order[]>([]);
  const [approvals, setApprovals] = useState<Approval[]>([]);
  const [review, setReview] = useState<Approval | null>(null);
  const [mode, setMode] = useState("success");
  const [view, setView] = useState<"orders" | "checkout">("orders");
  const [busy, setBusy] = useState(false);
  const [loaded, setLoaded] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [now, setNow] = useState(Date.now());
  const [selected, setSelected] = useState<string | null>(null);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      loadVersion.current++;
    };
  }, []);
  const load = useCallback(async () => {
    const version = ++loadVersion.current;
    try {
      const [nextOrders, nextApprovals] = await Promise.all([
        request<Order[]>("orders"),
        request<Approval[]>("actions"),
      ]);
      if (!mounted.current || version !== loadVersion.current) return;
      setOrders(nextOrders);
      setApprovals(nextApprovals);
      setLoaded(true);
    } catch (e) {
      if (mounted.current && version === loadVersion.current)
        setError(
          e instanceof Error ? e.message : "Orders could not be loaded.",
        );
    }
  }, []);
  useEffect(() => {
    if (open) {
      setNow(Date.now());
      dialog.current?.showModal();
      setView(checkout ? "checkout" : "orders");
      setReview(null);
      setError(null);
      setNotice(null);
      void load();
      const poll = window.setInterval(() => {
        if (!pending.current) void load();
      }, 5000);
      const tick = window.setInterval(() => setNow(Date.now()), 1000);
      return () => {
        clearInterval(poll);
        clearInterval(tick);
      };
    }
    dialog.current?.close();
  }, [open, checkout, load]);

  async function run(operation: () => Promise<void>) {
    if (pending.current) return;
    pending.current = true;
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await operation();
    } catch (e) {
      if (mounted.current) {
        setError(
          e instanceof Error
            ? e.message
            : "The request failed. Refresh to check its result.",
        );
        if (review) {
          try {
            const updated = await request<Approval>(`actions/${review.id}`);
            if (mounted.current) setReview(updated);
          } catch {
            /* Keep the original review for an idempotent retry. */
          }
        }
      }
    } finally {
      pending.current = false;
      if (mounted.current) {
        setBusy(false);
        await load();
      }
    }
  }
  async function prepare(path: string, body?: unknown) {
    await run(async () => {
      const action = await request<Approval>(path, "POST", body);
      if (mounted.current) {
        setNow(Date.now());
        setReview(action);
      }
    });
  }
  async function decide(confirm: boolean) {
    if (!review) return;
    await run(async () => {
      const result = await request<Approval>(
        `actions/${review.id}/${confirm ? "confirm" : "reject"}`,
        "POST",
        confirm ? { snapshotHash: review.snapshotHash } : {},
      );
      if (!mounted.current) return;
      setReview(null);
      setView("orders");
      setSelected(result.orderId);
      setNotice(
        confirm
          ? "Approval completed. The recorded order status is shown below."
          : "Review dismissed. No action was taken.",
      );
      await shop.refresh();
    });
  }
  const seconds = review
    ? Math.max(
        0,
        Math.ceil((new Date(review.expiresAt).getTime() - now) / 1000),
      )
    : 0;
  const valid = review?.status === "Pending" && seconds > 0;
  const current = orders.find((o) => o.id === selected) ?? orders[0];

  return (
    <dialog
      ref={dialog}
      className="orders-dialog"
      aria-labelledby="orders-title"
      onClose={onClose}
      onCancel={(event) => {
        if (busy) event.preventDefault();
      }}
    >
      <div className="orders-heading">
        <div>
          <div className="eyebrow">YOUR COMMERCEPILOT</div>
          <h2 id="orders-title">
            {review
              ? "One last look."
              : view === "checkout"
                ? "Make it yours."
                : "Your orders."}
          </h2>
        </div>
        <button
          className="bag-close"
          aria-label="Close orders"
          disabled={busy}
          onClick={onClose}
        >
          <X size={22} />
        </button>
      </div>
      <div className="demo-banner">
        <ShieldCheck size={17} />
        <span>
          A demo from start to finish. No real charges, cards or deliveries.
        </span>
      </div>
      {error && (
        <div role="alert" className="shopping-error">
          {error}
          <button
            className="text-button"
            disabled={busy}
            onClick={() =>
              void run(async () => {
                if (review)
                  setReview(await request<Approval>(`actions/${review.id}`));
                await load();
              })
            }
          >
            Refresh status
          </button>
        </div>
      )}
      {notice && (
        <p className="shopping-notice" role="status">
          <Check size={16} />
          {notice}
        </p>
      )}
      {review ? (
        <section className="approval-review">
          <div className="review-heading">
            <span className="status-pill">
              {review.kind === "Checkout"
                ? "ORDER APPROVAL"
                : `${review.kind.toUpperCase()} APPROVAL`}
            </span>
            <span className={`expiry ${valid ? "" : "expired"}`}>
              <Clock3 size={14} />
              {valid
                ? `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")} left to approve`
                : review.status === "Pending"
                  ? "Review expired"
                  : review.status}
            </span>
          </div>
          <h3>
            {review.kind === "Checkout"
              ? "Ready when you are."
              : review.kind === "Cancellation"
                ? "Cancel this order?"
                : "Request a full refund?"}
          </h3>
          <p className="order-description">
            {review.kind === "Checkout"
              ? "Check these exact items and the total. Your order is placed only when you approve below."
              : review.kind === "Cancellation"
                ? "Cancellation releases reserved inventory and refunds any captured demo payment. This action needs your approval."
                : "This returns the full captured demo payment. Delivered items are not automatically returned to inventory."}
          </p>
          <Lines
            items={review.snapshot.items}
            currency={review.snapshot.currency}
          />
          <div className="review-total">
            <span>
              {review.kind === "Refund" ? "Refund total" : "Order total"}
            </span>
            <strong>
              {money(review.snapshot.total, review.snapshot.currency)}
            </strong>
          </div>
          <div className="review-facts">
            <div>
              <Truck size={18} />
              <span>
                {review.snapshot.destination}
                <small>Fictional destination · shipping and tax $0.00</small>
              </span>
            </div>
            {review.kind === "Checkout" && (
              <div>
                <ShieldCheck size={18} />
                <span>
                  Payment simulation: {review.snapshot.paymentMode}
                  <small>
                    {review.snapshot.paymentMode === "delay"
                      ? "Payment completes after about 20 seconds."
                      : review.snapshot.paymentMode === "failure"
                        ? "Payment will fail. Stock is released and your bag stays available."
                        : "Payment completes immediately."}
                  </small>
                </span>
              </div>
            )}
          </div>
          {!valid && (
            <p className="shopping-error">
              {review.status === "Completed"
                ? "This action already completed. View your orders to see the result."
                : "This review can no longer be approved. Open a new review with the latest details."}
            </p>
          )}
          <div className="approval-buttons">
            <button
              className="button light"
              disabled={busy}
              onClick={() =>
                valid
                  ? void decide(false)
                  : (setReview(null), setView("orders"))
              }
            >
              {valid ? "Reject" : "Back to orders"}
            </button>
            <button
              className="button dark"
              disabled={busy || !valid}
              onClick={() => void decide(true)}
            >
              {busy ? (
                <LoaderCircle size={17} className="spin" />
              ) : (
                <ShieldCheck size={17} />
              )}
              {review.kind === "Checkout"
                ? `Approve ${money(review.snapshot.total, review.snapshot.currency)} order`
                : review.kind === "Cancellation"
                  ? "Approve cancellation"
                  : "Approve refund"}
            </button>
          </div>
          <p className="approval-footnote">
            Prices, stock and eligibility are checked again when you approve.
            Changes require a fresh review.
          </p>
        </section>
      ) : view === "checkout" ? (
        <section className="checkout-setup">
          <div className="checkout-illustration">
            <Package size={56} strokeWidth={1} />
            <span>Good things, on their way.</span>
          </div>
          <h3>Try the full journey.</h3>
          <p className="order-description">
            Choose how your demo payment behaves, then review your order.
            Nothing is reserved or charged during review.
          </p>
          <fieldset className="payment-modes">
            <legend>Demo payment outcome</legend>
            {[
              [
                "success",
                "Successful payment",
                "Instant confirmation. Ready for dispatch in 2 minutes.",
              ],
              [
                "delay",
                "Delayed payment",
                "A 20-second wait before the payment completes.",
              ],
              [
                "failure",
                "Declined payment",
                "See a failed payment with your bag preserved.",
              ],
            ].map(([value, title, detail]) => (
              <label key={value} className={mode === value ? "chosen" : ""}>
                <input
                  type="radio"
                  name="payment-mode"
                  value={value}
                  checked={mode === value}
                  onChange={() => setMode(value)}
                  disabled={busy}
                />
                <span>
                  <strong>{title}</strong>
                  <small>{detail}</small>
                </span>
              </label>
            ))}
          </fieldset>
          <button
            className="button dark full-width"
            disabled={busy || !shop.ready || !shop.cart?.isAvailable}
            onClick={() =>
              void prepare("orders/prepare", { paymentMode: mode })
            }
          >
            {busy ? (
              <LoaderCircle className="spin" size={17} />
            ) : (
              <ArrowRight size={17} />
            )}
            Review exact order
          </button>
          {!shop.cart?.isAvailable && (
            <p className="shopping-error">
              Your bag is empty or needs a quantity update before checkout.
            </p>
          )}
          <button
            className="text-button"
            disabled={busy}
            onClick={() => setView("orders")}
          >
            <ArrowLeft size={14} /> Back to orders
          </button>
        </section>
      ) : (
        <>
          <div className="orders-toolbar">
            <span>
              {orders.length} recent {orders.length === 1 ? "order" : "orders"}
            </span>
            <button
              className="text-button"
              disabled={busy}
              onClick={() => void run(load)}
            >
              <RefreshCw size={14} />
              Refresh
            </button>
          </div>
          {approvals.length > 0 && (
            <div className="pending-reviews">
              <strong>Waiting for your review</strong>
              {approvals.map((a) => (
                <button
                  key={a.id}
                  disabled={busy}
                  onClick={() => {
                    setReview(a);
                    setNow(Date.now());
                  }}
                >
                  <span>
                    {a.kind === "Checkout" ? "New order" : a.kind} ·{" "}
                    {money(a.snapshot.total, a.snapshot.currency)}
                  </span>
                  <ArrowRight size={16} />
                </button>
              ))}
            </div>
          )}
          {!loaded ? (
            <p role="status" className="bag-empty">
              Loading your orders…
            </p>
          ) : orders.length === 0 ? (
            <div className="bag-empty">
              <Package size={44} strokeWidth={1} />
              <h3>Your next favorite starts here.</h3>
              <p>
                Approved orders will appear here with their payment and delivery
                history.
              </p>
              <button className="button dark" onClick={onClose}>
                Explore the collection <ArrowRight size={15} />
              </button>
            </div>
          ) : (
            <div className="order-workspace">
              <nav className="order-list" aria-label="Order history">
                {orders.map((order) => (
                  <button
                    key={order.id}
                    className={current?.id === order.id ? "selected" : ""}
                    onClick={() => setSelected(order.id)}
                    aria-pressed={current?.id === order.id}
                  >
                    <span>
                      <strong>{order.number}</strong>
                      <small>{date(order.createdAt)}</small>
                    </span>
                    <span
                      className={`status-pill status-${order.status.toLowerCase()}`}
                    >
                      {label(order.status)}
                    </span>
                    <span>
                      {order.items.reduce((n, i) => n + i.quantity, 0)}{" "}
                      {order.items.reduce((n, i) => n + i.quantity, 0) === 1 ? "item" : "items"}{" "}
                      <b>{money(order.total, order.currency)}</b>
                    </span>
                  </button>
                ))}
              </nav>
              {current && (
                <section
                  className="order-detail"
                  aria-label={`Details for ${current.number}`}
                >
                  <div className="order-detail-heading">
                    <div>
                      <span className="eyebrow">ORDER DETAILS</span>
                      <h3>{current.number}</h3>
                    </div>
                    <span
                      className={`status-pill status-${current.status.toLowerCase()}`}
                    >
                      {label(current.status)}
                    </span>
                  </div>
                  <Lines items={current.items} currency={current.currency} />
                  <div className="review-total">
                    <span>Total</span>
                    <strong>{money(current.total, current.currency)}</strong>
                  </div>
                  <div className="payment-ledger">
                    <span>
                      Payment <strong>{current.paymentStatus}</strong>
                    </span>
                    <span>
                      Captured{" "}
                      <strong>
                        {money(current.capturedAmount, current.currency)}
                      </strong>
                    </span>
                    <span>
                      Refunded{" "}
                      <strong>
                        {money(current.refundedAmount, current.currency)}
                      </strong>
                    </span>
                  </div>
                  <p className="destination">
                    <Truck size={16} />
                    {current.destination}
                  </p>
                  <h4 className="timeline-title">The journey so far</h4>
                  <ol className="order-timeline">
                    {current.timeline.map((event, index) => (
                      <li key={`${event.at}-${index}`}>
                        <span className="timeline-dot">
                          <Check size={11} />
                        </span>
                        <div>
                          <strong>{event.title}</strong>
                          <time>{date(event.at)}</time>
                          <p>{event.detail}</p>
                        </div>
                      </li>
                    ))}
                  </ol>
                  {current.nextTransitionAt && (
                    <p className="next-transition">
                      <Clock3 size={15} />
                      Next demo update around {date(current.nextTransitionAt)}.
                      Updates appear automatically.
                    </p>
                  )}
                  <div className="order-actions">
                    {current.canCancel && (
                      <button
                        className="button light"
                        disabled={busy}
                        onClick={() =>
                          void prepare(
                            `orders/${current.id}/prepare-cancellation`,
                          )
                        }
                      >
                        Review cancellation
                      </button>
                    )}
                    {current.canRefund && (
                      <button
                        className="button light"
                        disabled={busy}
                        onClick={() =>
                          void prepare(`orders/${current.id}/prepare-refund`)
                        }
                      >
                        Review refund
                      </button>
                    )}
                  </div>
                  <p className="approval-footnote">
                    Cancel before dispatch. Full demo refunds are available for
                    30 days after delivery.
                  </p>
                </section>
              )}
            </div>
          )}
        </>
      )}
    </dialog>
  );
}

function Lines({ items, currency }: { items: Line[]; currency: string }) {
  return (
    <ul className="order-lines">
      {items.map((item) => (
        <li key={item.productId}>
          <div className={`order-art ${item.color}`}>
            <ProductArt kind={item.artwork} />
          </div>
          <div>
            <strong>{item.name}</strong>
            <span>
              {item.quantity} × {money(item.unitPrice, currency)}
            </span>
          </div>
          <b>{money(item.quantity * item.unitPrice, currency)}</b>
        </li>
      ))}
    </ul>
  );
}
