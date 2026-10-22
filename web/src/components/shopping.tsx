"use client";

import {
  createContext,
  FormEvent,
  ReactNode,
  useCallback,
  useContext,
  useEffect,
  useRef,
  useState,
} from "react";
import {
  ArrowRight,
  Check,
  Package,
  LoaderCircle,
  LogOut,
  Minus,
  Plus,
  ShoppingBag,
  Trash2,
  UserRound,
  X,
} from "lucide-react";
import { money, Product } from "@/lib/catalog";
import { ProductArt } from "./product-art";
import { ChatPanel } from "./chat";
import Link from "next/link";

type User = { id: string; email: string; displayName: string; isAdmin: boolean };
type CartItem = {
  productId: string;
  name: string;
  category: string;
  artwork: string;
  color: string;
  quantity: number;
  unitPrice: number;
  currency: string;
  availableStock: number;
  lineTotal: number;
  isAvailable: boolean;
};
type Cart = {
  items: CartItem[];
  itemCount: number;
  total: number;
  currency: string;
  isAvailable: boolean;
};
type ShoppingState = { user: User | null; cart: Cart | null; ready: boolean };
type ShoppingContextValue = ShoppingState & {
  busy: boolean;
  error: string | null;
  notice: string | null;
  accountOpen: boolean;
  bagOpen: boolean;
  setAccountOpen: (value: boolean) => void;
  setBagOpen: (value: boolean) => void;
  chatOpen: boolean;
  chatDraft: string;
  setChatOpen: (value: boolean) => void;
  setChatDraft: (value: string) => void;
  openChat: (message?: string) => void;
  refresh: () => Promise<void>;
  login: (email: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
  add: (product: Product, quantity: number) => Promise<void>;
  update: (productId: string, quantity: number) => Promise<void>;
  remove: (productId: string) => Promise<void>;
};
const ShoppingContext = createContext<ShoppingContextValue | null>(null);

class ShopError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
  }
}

export async function request<T>(
  path: string,
  method = "GET",
  body?: unknown,
): Promise<T> {
  const headers: Record<string, string> = {};
  if (method !== "GET") {
    // Tokens are identity-bound. Fetch a fresh token after login/logout or another tab's session change.
    const csrfResponse = await fetch("/api/shop/auth/csrf", {
      cache: "no-store",
    });
    if (!csrfResponse.ok)
      throw new ShopError(
        "We couldn’t establish a secure session. Please try again.",
        csrfResponse.status,
      );
    const csrf = await csrfResponse.json();
    headers["X-CSRF-Token"] = csrf.token;
    headers["Content-Type"] = "application/json";
  }
  const response = await fetch(`/api/shop/${path}`, {
    method,
    headers,
    cache: "no-store",
    body:
      method === "GET" || method === "DELETE"
        ? undefined
        : JSON.stringify(body ?? {}),
  });
  const result =
    response.status === 204 ? null : await response.json().catch(() => null);
  if (!response.ok)
    throw new ShopError(
      result?.detail ?? "Something went wrong. Refresh your bag and try again.",
      response.status,
    );
  return result as T;
}

export function ShoppingProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<ShoppingState>({
    user: null,
    cart: null,
    ready: false,
  });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [accountOpen, setAccountOpen] = useState(false);
  const [bagOpen, setBagOpen] = useState(false);
  const [chatOpen, setChatOpen] = useState(false);
  const [chatDraft, setChatDraft] = useState("");
  const generation = useRef(0);
  const mutationPending = useRef(false);
  const channel = useRef<BroadcastChannel | null>(null);

  const refresh = useCallback(async () => {
    const version = ++generation.current;
    setState((current) => ({ ...current, ready: false }));
    try {
      const session = await request<{ user: User | null }>("auth/session");
      const cart = session.user ? await request<Cart>("cart") : null;
      if (version === generation.current) {
        setState({ user: session.user, cart, ready: true });
        setError(null);
      }
    } catch (e) {
      if (version === generation.current) {
        setState({ user: null, cart: null, ready: true });
        setError(
          e instanceof Error
            ? e.message
            : "Your session is unavailable. Please refresh.",
        );
      }
    }
  }, []);

  useEffect(() => {
    void refresh();
    channel.current = new BroadcastChannel("commercepilot-session");
    channel.current.onmessage = () => {
      setNotice(null);
      void refresh();
    };
    const onFocus = () => {
      if (!mutationPending.current) void refresh();
    };
    window.addEventListener("focus", onFocus);
    return () => {
      generation.current++;
      channel.current?.close();
      window.removeEventListener("focus", onFocus);
    };
  }, [refresh]);

  async function run(action: () => Promise<void>) {
    if (mutationPending.current) return;
    mutationPending.current = true;
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await action();
    } catch (e) {
      if (e instanceof ShopError && e.status === 401) {
        generation.current++;
        setState({ user: null, cart: null, ready: true });
      }
      setError(
        e instanceof Error
          ? e.message
          : "Something went wrong. Please try again.",
      );
    } finally {
      mutationPending.current = false;
      setBusy(false);
    }
  }

  async function login(email: string, password: string) {
    await run(async () => {
      await request("auth/login", "POST", { email, password });
      await refresh();
      channel.current?.postMessage("session-changed");
      setAccountOpen(false);
    });
  }
  async function logout() {
    await run(async () => {
      await request("auth/logout", "POST");
      generation.current++;
      setState({ user: null, cart: null, ready: true });
      setAccountOpen(false);
      setBagOpen(false);
      channel.current?.postMessage("session-changed");
    });
  }
  async function changeCart(
    path: string,
    method: string,
    body: unknown,
    message: string,
  ) {
    await run(async () => {
      const version = generation.current;
      const cart = await request<Cart>(path, method, body);
      if (version === generation.current) {
        setState((current) => ({ ...current, cart }));
        setNotice(message);
        channel.current?.postMessage("cart-changed");
      }
    });
  }
  async function add(product: Product, quantity: number) {
    if (!state.user) {
      setAccountOpen(true);
      return;
    }
    await changeCart(
      "cart/items",
      "POST",
      { productId: product.id, quantity },
      `${product.name} added to your bag.`,
    );
  }

  return (
    <ShoppingContext.Provider
      value={{
        ...state,
        busy,
        error,
        notice,
        accountOpen,
        bagOpen,
        setAccountOpen,
        setBagOpen,
        chatOpen,
        chatDraft,
        setChatOpen,
        setChatDraft,
        openChat: (message = "") => {
          setChatDraft(message);
          setChatOpen(true);
        },
        refresh,
        login,
        logout,
        add,
        update: (id, quantity) =>
          changeCart(
            `cart/items/${id}`,
            "PUT",
            { quantity },
            "Your bag is updated.",
          ),
        remove: (id) =>
          changeCart(
            `cart/items/${id}`,
            "DELETE",
            undefined,
            "Item removed from your bag.",
          ),
      }}
    >
      {children}
    </ShoppingContext.Provider>
  );
}

export function useShopping() {
  const context = useContext(ShoppingContext);
  if (!context) throw new Error("ShoppingProvider is required.");
  return context;
}

export function ShoppingControls() {
  const shop = useShopping();
  const accountDialog = useRef<HTMLDialogElement>(null);
  const bagDialog = useRef<HTMLDialogElement>(null);
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  useEffect(() => {
    if (shop.accountOpen) accountDialog.current?.showModal();
    else {
      accountDialog.current?.close();
      setPassword("");
    }
  }, [shop.accountOpen]);
  useEffect(() => {
    if (shop.bagOpen) bagDialog.current?.showModal();
    else bagDialog.current?.close();
  }, [shop.bagOpen]);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    await shop.login(email, password);
    setPassword("");
  }

  return (
    <>
      <div className="shopping-controls">
        {shop.user?.isAdmin && <Link className="account-trigger" href="/admin" onClick={() => shop.setChatOpen(false)}>Admin</Link>}
        {shop.user && (
          <Link
            className="account-trigger"
            href="/orders"
            aria-label="My orders"
              onClick={() => shop.setChatOpen(false)}
          >
            <Package size={17} />
            <span>Orders</span>
          </Link>
        )}
        <button
          className="account-trigger"
          onClick={() => shop.setAccountOpen(true)}
          disabled={!shop.ready}
          aria-label={
            shop.user ? `Account: ${shop.user.displayName}` : "Sign in"
          }
        >
          <UserRound size={17} />
          <span>{shop.user?.displayName ?? "Sign in"}</span>
        </button>
        <button
          className="bag-trigger"
          aria-label={`Shopping bag, ${shop.cart?.itemCount ?? 0} items`}
          onClick={() => shop.setBagOpen(true)}
        >
          <ShoppingBag size={18} />
          <span>{shop.cart?.itemCount ?? 0}</span>
        </button>
      </div>

      <dialog
        ref={accountDialog}
        className="account-dialog"
        aria-labelledby="account-title"
        onClose={() => shop.setAccountOpen(false)}
        onClick={(event) => {
          if (event.target === event.currentTarget && !shop.busy)
            shop.setAccountOpen(false);
        }}
      >
        <button
          className="dialog-close"
          aria-label="Close account"
          onClick={() => shop.setAccountOpen(false)}
        >
          <X size={19} />
        </button>
        <div className="account-symbol">
          <UserRound size={23} />
        </div>
        <div className="eyebrow">YOUR EVERYDAY, ALL IN ONE PLACE</div>
        <h2 id="account-title">
          {shop.user
            ? `Hello, ${shop.user.displayName}.`
            : "Make yourself at home."}
        </h2>
        {shop.user ? (
          <>
            <p>Your bag is saved to your account, ready when you are.</p>
            <div className="account-email">{shop.user.email}</div>
            <button
              className="button dark full-width"
              disabled={shop.busy}
              onClick={() => void shop.logout()}
            >
              <LogOut size={16} />
              {shop.busy ? "Signing out…" : "Sign out"}
            </button>
          </>
        ) : (
          <>
            <p>Sign in to keep your favorites together in your bag.</p>
            <form onSubmit={submit} className="signin-form">
              <label>
                Email address
                <input
                  autoComplete="username"
                  type="email"
                  required
                  maxLength={254}
                  value={email}
                  onChange={(event) => setEmail(event.target.value)}
                  placeholder="you@example.com"
                />
              </label>
              <label>
                Password
                <input
                  autoComplete="current-password"
                  type="password"
                  required
                  maxLength={128}
                  value={password}
                  onChange={(event) => setPassword(event.target.value)}
                  placeholder="Enter your password"
                />
              </label>
              <button
                className="button dark full-width"
                type="submit"
                disabled={shop.busy || !shop.ready}
              >
                {shop.busy ? (
                  <>
                    <LoaderCircle size={16} className="spin" /> Signing in…
                  </>
                ) : (
                  <>
                    Sign in <ArrowRight size={16} />
                  </>
                )}
              </button>
            </form>
            <div className="demo-accounts">
              <span>EXPLORING THE DEMO?</span>
              <p>Choose a local demo account, then enter its password.</p>
              <div>
                <button
                  onClick={() => setEmail("alex@commercepilot.local")}
                  disabled={shop.busy}
                >
                  Alex
                </button>
                <button
                  onClick={() => setEmail("sam@commercepilot.local")}
                  disabled={shop.busy}
                >
                  Sam
                </button>
              </div>
              <small>
                Demo credentials are provided with your local setup.
              </small>
            </div>
          </>
        )}
        {shop.error && (
          <p className="shopping-error" role="alert">
            {shop.error}
          </p>
        )}
      </dialog>

      <dialog
        ref={bagDialog}
        className="bag-dialog"
        aria-labelledby="bag-title"
        onClose={() => shop.setBagOpen(false)}
        onClick={(event) => {
          if (event.target === event.currentTarget) shop.setBagOpen(false);
        }}
      >
        <div className="bag-content">
          <div className="bag-heading">
            <div>
              <div className="eyebrow">A FEW GOOD THINGS</div>
              <h2 id="bag-title">
                Your bag<span>({shop.cart?.itemCount ?? 0})</span>
              </h2>
            </div>
            <button
              className="bag-close"
              aria-label="Close shopping bag"
              onClick={() => shop.setBagOpen(false)}
              autoFocus
            >
              <X size={21} />
            </button>
          </div>
          {!shop.ready ? (
            <div className="bag-empty" role="status">
              <LoaderCircle className="spin" />
              <p>Loading your bag…</p>
            </div>
          ) : !shop.user ? (
            <div className="bag-empty">
              <ShoppingBag size={35} strokeWidth={1.2} />
              <h3>A place for your next favorites.</h3>
              <p>Sign in to create a bag that stays with you.</p>
              <button
                className="button dark"
                onClick={() => {
                  shop.setBagOpen(false);
                  shop.setAccountOpen(true);
                }}
              >
                Sign in <ArrowRight size={16} />
              </button>
            </div>
          ) : !shop.cart?.items.length ? (
            <div className="bag-empty">
              <ShoppingBag size={35} strokeWidth={1.2} />
              <h3>Good things start here.</h3>
              <p>Your bag is empty. Find something that feels like you.</p>
              <button
                className="button dark"
                onClick={() => shop.setBagOpen(false)}
              >
                Keep exploring <ArrowRight size={16} />
              </button>
            </div>
          ) : (
            <>
              <div className="bag-owner">
                Saved for {shop.user.displayName} <span>{shop.user.email}</span>
              </div>
              <ul className="bag-items">
                {shop.cart.items.map((item) => (
                  <li className="bag-item" key={item.productId}>
                    <div className={`bag-art ${item.color}`}>
                      <ProductArt kind={item.artwork} />
                    </div>
                    <div className="bag-item-copy">
                      <span className="eyebrow">{item.category}</span>
                      <h3>{item.name}</h3>
                      <p>{money(item.unitPrice, item.currency)} each</p>
                      <div className="bag-item-actions">
                        <div className="quantity-control">
                          <button
                            aria-label={`Decrease ${item.name} quantity`}
                            disabled={shop.busy || item.quantity <= 1}
                            onClick={() =>
                              void shop.update(
                                item.productId,
                                item.quantity - 1,
                              )
                            }
                          >
                            <Minus size={13} />
                          </button>
                          <span aria-label={`${item.name} quantity`}>
                            {item.quantity}
                          </span>
                          <button
                            aria-label={`Increase ${item.name} quantity`}
                            disabled={
                              shop.busy ||
                              item.quantity >= Math.min(99, item.availableStock)
                            }
                            onClick={() =>
                              void shop.update(
                                item.productId,
                                item.quantity + 1,
                              )
                            }
                          >
                            <Plus size={13} />
                          </button>
                        </div>
                        <button
                          className="remove-item"
                          aria-label={`Remove ${item.name}`}
                          disabled={shop.busy}
                          onClick={() => void shop.remove(item.productId)}
                        >
                          <Trash2 size={14} />
                        </button>
                      </div>
                      {!item.isAvailable && (
                        <p className="stock-warning">
                          Only {item.availableStock} available. Adjust or remove
                          this item.
                        </p>
                      )}
                    </div>
                    <strong className="bag-line-total">
                      {money(item.lineTotal, item.currency)}
                    </strong>
                  </li>
                ))}
              </ul>
              <div className="bag-summary">
                <div>
                  <span>Subtotal</span>
                  <strong>{money(shop.cart.total, shop.cart.currency)}</strong>
                </div>
                <p>
                  Prices and availability are checked by the store. Items in
                  your bag are not reserved.
                </p>
                <div className="checkout-notice">
                  Demo checkout. No real charges or deliveries.
                </div>
                <button
                  className="button dark full-width"
                  disabled={shop.busy || !shop.cart.isAvailable}
                  onClick={() => {
                    shop.setBagOpen(false);
                    shop.openChat("Prepare checkout for my bag.");
                  }}
                >
                  Review in chat <ArrowRight size={16} />
                </button>
                <button
                  className="button light full-width"
                  onClick={() => shop.setBagOpen(false)}
                >
                  Continue exploring <ArrowRight size={16} />
                </button>
              </div>
            </>
          )}
          {shop.notice && (
            <p className="shopping-notice" role="status">
              <Check size={14} />
              {shop.notice}
            </p>
          )}
          {shop.error && (
            <div className="shopping-error" role="alert">
              <p>{shop.error}</p>
              <button
                className="text-button"
                disabled={shop.busy}
                onClick={() => void shop.refresh()}
              >
                Refresh bag
              </button>
            </div>
          )}
        </div>
      </dialog>
      <ChatPanel key={shop.user?.id ?? "guest"} />
    </>
  );
}

export function AddToBag({ product }: { product: Product }) {
  const shop = useShopping();
  const [quantity, setQuantity] = useState(1);
  const previousNotice = useRef(shop.notice);
  const [added, setAdded] = useState(false);
  useEffect(() => {
    setQuantity(1);
    setAdded(false);
  }, [product.id, shop.user?.id]);
  useEffect(() => {
    if (
      shop.notice &&
      shop.notice !== previousNotice.current &&
      shop.notice === `${product.name} added to your bag.`
    )
      setAdded(true);
    previousNotice.current = shop.notice;
  }, [shop.notice, product.name]);
  return (
    <div className="purchase-block">
      <div className="purchase-row">
        <label className="purchase-quantity">
          Quantity
          <select
            aria-label="Quantity to add"
            value={quantity}
            disabled={shop.busy || product.stock === 0}
            onChange={(event) => {
              setQuantity(Number(event.target.value));
              setAdded(false);
            }}
          >
            {Array.from(
              { length: Math.min(99, Math.max(1, product.stock)) },
              (_, index) => (
                <option value={index + 1} key={index}>
                  {index + 1}
                </option>
              ),
            )}
          </select>
        </label>
        <button
          className="button dark"
          disabled={shop.busy || !shop.ready || product.stock === 0}
          onClick={() => {
            setAdded(false);
            void shop.add(product, quantity);
          }}
        >
          {shop.busy ? (
            <LoaderCircle size={16} className="spin" />
          ) : (
            <ShoppingBag size={16} />
          )}
          {product.stock === 0
            ? "Out of stock"
            : shop.user
              ? "Add to bag"
              : "Sign in to add"}
        </button>
      </div>
      {added && shop.user && (
        <div className="shopping-notice" role="status">
          <Check size={14} />
          Added to your bag.
          <button onClick={() => shop.setBagOpen(true)}>
            View bag <ArrowRight size={12} />
          </button>
        </div>
      )}
      {shop.error && (
        <p className="shopping-error" role="alert">
          {shop.error}
        </p>
      )}
    </div>
  );
}
