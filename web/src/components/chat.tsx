"use client";

import { FormEvent, useCallback, useEffect, useRef, useState } from "react";
import {
  ArrowUp,
  AlertCircle,
  Check,
  LoaderCircle,
  MessageCircle,
  Plus,
  RefreshCw,
  Sparkles,
  Square,
  Trash2,
  X,
} from "lucide-react";
import { request, useShopping } from "./shopping";
import { Approval, Card, ChatCardView } from "./chat-cards";

type Conversation = { id: string; title: string; activeTurnId?: string | null };
type ChatEvent = {
  sequence: number;
  conversationId: string;
  turnId: string;
  kind: string;
  data: Record<string, unknown>;
};
type Turn = { id: string; input: string; status: string };
type History = {
  conversation: Conversation;
  turns: Turn[];
  events: ChatEvent[];
  cards: Card[];
};
type Entry = {
  key: string;
  kind: "text" | "card" | "error" | "progress";
  text?: string;
  card?: Card;
  done?: boolean;
  failed?: boolean;
};
const activities: Record<string, string> = {
  search_products: "Finding your essentials",
  get_product: "Taking a closer look",
  get_cart: "Checking your bag",
  add_to_cart: "Updating your bag",
  update_cart_item: "Updating quantities",
  remove_from_cart: "Updating your bag",
  get_my_orders: "Looking up your orders",
  get_order_status: "Checking your order",
  prepare_order: "Preparing your review",
  prepare_cancellation: "Preparing a cancellation review",
  prepare_refund: "Preparing a refund review",
  get_pending_action_status: "Checking your review",
};

function entries(
  turnId: string,
  events: ChatEvent[],
  cards: Card[],
  status: string,
): Entry[] {
  const result: Entry[] = [];
  for (const event of events.filter((e) => e.turnId === turnId)) {
    const d = event.data;
    if (event.kind === "message.delta") {
      const key = String(d.messageId);
      const existing = result.find((item) => item.key === key);
      if (existing) existing.text += String(d.text);
      else result.push({ key, kind: "text", text: String(d.text) });
    } else if (event.kind === "tool.started")
      result.push({
        key: String(d.toolCallId),
        kind: "progress",
        text: activities[String(d.name)] ?? "Checking with the store",
      });
    else if (event.kind === "tool.completed") {
      const progress = result.find((item) => item.key === String(d.toolCallId));
      if (progress) {
        progress.done = true;
        progress.failed = d.isError === true;
      }
    } else if (event.kind === "card.upsert") {
      const id = String(d.cardId);
      const card = cards.find((c) => c.cardId === id) ?? (d as unknown as Card);
      if (!result.some((item) => item.key === id))
        result.push({ key: id, kind: "card", card });
    } else if (event.kind === "turn.failed")
      result.push({
        key: String(event.sequence),
        kind: "error",
        text: String(d.message),
      });
  }
  if (status === "Failed" || status === "Interrupted") {
    for (const entry of result)
      if (entry.kind === "progress" && !entry.done) entry.failed = true;
  }
  return result;
}

export function ChatPanel() {
  const shop = useShopping();
  const [list, setList] = useState<Conversation[]>([]);
  const [history, setHistory] = useState<History | null>(null);
  const [input, setInput] = useState("");
  const [loading, setLoading] = useState(false);
  const [sending, setSending] = useState(false);
  const [decisionBusy, setDecisionBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [now, setNow] = useState(Date.now());
  const controller = useRef<AbortController | null>(null);
  const pending = useRef(false);
  const generation = useRef(0);
  const scroll = useRef<HTMLDivElement>(null);
  const textArea = useRef<HTMLTextAreaElement>(null);
  const autoScroll = useRef(true);
  const storeKey = `commercepilot-chat-${shop.user?.id ?? "guest"}`;
  const busy =
    sending || decisionBusy || loading || !!history?.conversation.activeTurnId;

  const load = useCallback(
    async (id: string) => {
      const version = ++generation.current;
      const data = await request<History>(`chat/conversations/${id}`);
      if (generation.current === version) {
        setHistory(data);
        localStorage.setItem(storeKey, id);
      }
      return data;
    },
    [storeKey],
  );
  useEffect(
    () => () => {
      generation.current++;
      controller.current?.abort();
    },
    [],
  );
  useEffect(() => {
    if (!shop.chatOpen || !shop.user || history) return;
    let disposed = false;
    setLoading(true);
    request<Conversation[]>("chat/conversations")
      .then(async (conversations) => {
        if (disposed) return;
        setList(conversations);
        const saved = localStorage.getItem(storeKey);
        const selected =
          conversations.find((c) => c.id === saved) ?? conversations[0];
        if (selected) await load(selected.id);
      })
      .catch((e) => {
        if (!disposed) setError(e.message);
      })
      .finally(() => {
        if (!disposed) setLoading(false);
      });
    return () => {
      disposed = true;
    };
  }, [shop.chatOpen, shop.user, history, load, storeKey]);
  useEffect(() => {
    if (shop.chatDraft && shop.chatOpen) {
      setInput(shop.chatDraft);
      shop.setChatDraft("");
    }
  }, [shop]);
  useEffect(() => {
    if (!shop.chatOpen) return;
    textArea.current?.focus();
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [shop.chatOpen]);
  useEffect(() => {
    if (!shop.chatOpen || !history || sending || decisionBusy) return;
    const timer = setInterval(
      () => {
        if (document.visibilityState === "visible")
          void load(history.conversation.id).catch(() => {});
      },
      history.conversation.activeTurnId ? 2000 : 15000,
    );
    return () => clearInterval(timer);
  }, [
    shop.chatOpen,
    history?.conversation.id,
    history?.conversation.activeTurnId,
    sending,
    decisionBusy,
    load,
  ]);
  useEffect(() => {
    if (autoScroll.current && scroll.current)
      scroll.current.scrollTop = scroll.current.scrollHeight;
  }, [history?.events.length, sending]);

  async function startNew() {
    if (busy) return;
    setError(null);
    setLoading(true);
    try {
      const c = await request<Conversation>("chat/conversations", "POST");
      setList((v) => [c, ...v]);
      await load(c.id);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  }
  async function removeConversation() {
    if (!history || busy) return;
    setLoading(true);
    try {
      await request(`chat/conversations/${history.conversation.id}`, "DELETE");
      localStorage.removeItem(storeKey);
      setHistory(null);
      setList((v) => v.filter((c) => c.id !== history.conversation.id));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  }
  async function send(text: string) {
    if (!text.trim() || busy || pending.current) return;
    pending.current = true;
    generation.current++;
    setSending(true);
    setError(null);
    setInput("");
    autoScroll.current = true;
    controller.current = new AbortController();
    let id = history?.conversation.id;
    const requestId = crypto.randomUUID();
    try {
      if (!id) {
        const created = await request<Conversation>(
          "chat/conversations",
          "POST",
        );
        id = created.id;
        setList((v) => [created, ...v]);
        setHistory({ conversation: created, turns: [], events: [], cards: [] });
        localStorage.setItem(storeKey, id);
      }
      setHistory(
        (h) =>
          h && {
            ...h,
            turns: [
              ...h.turns,
              { id: requestId, input: text.trim(), status: "Running" },
            ],
          },
      );
      const csrf = await request<{ token: string }>("auth/csrf");
      const response = await fetch(
        `/api/shop/chat/conversations/${id}/messages`,
        {
          method: "POST",
          credentials: "same-origin",
          cache: "no-store",
          headers: {
            "Content-Type": "application/json",
            "X-CSRF-Token": csrf.token,
          },
          body: JSON.stringify({ requestId, message: text.trim() }),
          signal: controller.current.signal,
        },
      );
      if (!response.ok)
        throw new Error(
          (await response.json()).detail ?? "The assistant could not respond.",
        );
      if (response.headers.get("content-type")?.includes("application/json")) {
        setHistory(await response.json());
        return;
      }
      const reader = response.body!.getReader();
      const decoder = new TextDecoder();
      let buffer = "";
      for (;;) {
        const chunk = await reader.read();
        if (chunk.done) break;
        buffer += decoder.decode(chunk.value, { stream: true });
        let boundary;
        while ((boundary = buffer.indexOf("\n\n")) >= 0) {
          const frame = buffer.slice(0, boundary);
          buffer = buffer.slice(boundary + 2);
          const data = frame
            .split("\n")
            .find((line) => line.startsWith("data: "));
          if (!data) continue;
          const event: ChatEvent = JSON.parse(data.slice(6));
          setHistory((h) => {
            if (
              !h ||
              h.conversation.id !== event.conversationId ||
              h.events.some((e) => e.sequence === event.sequence)
            )
              return h;
            const cards =
              event.kind === "card.upsert"
                ? [
                    ...h.cards.filter((c) => c.cardId !== event.data.cardId),
                    event.data as unknown as Card,
                  ]
                : h.cards;
            return { ...h, events: [...h.events, event], cards };
          });
        }
      }
    } catch (e) {
      setError(
        (e as Error).name === "AbortError"
          ? "Reply stopped. Submitted changes may still have completed; check the saved result before repeating them."
          : (e as Error).message,
      );
    } finally {
      if (id)
        await load(id).catch(() =>
          setError(
            "Connection lost. Refresh this conversation to recover the saved result; no change will be replayed.",
          ),
        );
      await request<Conversation[]>("chat/conversations")
        .then(setList)
        .catch(() => {});
      await shop.refresh();
      pending.current = false;
      setSending(false);
      controller.current = null;
      textArea.current?.focus();
    }
  }
  async function decide(card: Card, decision: "approve" | "reject") {
    if (pending.current || busy || !history) return;
    pending.current = true;
    generation.current++;
    setDecisionBusy(true);
    setError(null);
    try {
      setHistory(
        await request<History>(
          `chat/conversations/${history.conversation.id}/cards/${card.cardId}/decision`,
          "POST",
          {
            requestId: crypto.randomUUID(),
            decision,
            snapshotHash: (card.payload as Approval).snapshotHash,
          },
        ),
      );
    } catch (e) {
      setError((e as Error).message);
      await load(history.conversation.id).catch(() => {});
    } finally {
      await shop.refresh();
      pending.current = false;
      setDecisionBusy(false);
    }
  }
  async function pendingReviews() {
    if (busy || pending.current) return;
    pending.current = true;
    generation.current++;
    setLoading(true);
    setError(null);
    try {
      let id = history?.conversation.id;
      if (!id) {
        const created = await request<Conversation>(
          "chat/conversations",
          "POST",
        );
        id = created.id;
        setList((v) => [created, ...v]);
      }
      setHistory(
        await request<History>(`chat/conversations/${id}/reviews`, "POST"),
      );
      localStorage.setItem(storeKey, id);
      autoScroll.current = true;
    } catch (e) {
      setError((e as Error).message);
    } finally {
      pending.current = false;
      setLoading(false);
    }
  }
  function submit(e: FormEvent) {
    e.preventDefault();
    void send(input);
  }

  return (
    <>
      {!shop.chatOpen && (
        <button className="chat-launcher" onClick={() => shop.openChat()}>
          <Sparkles size={19} />
          <span>Ask CommercePilot</span>
          <span className="chat-live-dot" />
        </button>
      )}
      {shop.chatOpen && (
        <aside
          className="chat-panel"
          role="dialog"
          aria-label="CommercePilot shopping assistant"
          onKeyDown={(e) => {
            if (e.key === "Escape") shop.setChatOpen(false);
          }}
        >
          <header className="chat-header">
            <div className="chat-brand-mark">
              <Sparkles size={22} />
            </div>
            <div>
              <h2>CommercePilot</h2>
              <span>Your thoughtful shopping companion</span>
            </div>
            <button
              aria-label="Close chat"
              onClick={() => shop.setChatOpen(false)}
            >
              <X size={19} />
            </button>
          </header>
          {shop.user ? (
            <>
              <nav className="chat-toolbar" aria-label="Conversations">
                <select
                  aria-label="Conversation"
                  value={history?.conversation.id ?? ""}
                  disabled={busy}
                  onChange={(e) => {
                    setError(null);
                    setLoading(true);
                    void load(e.target.value)
                      .catch((err) => setError(err.message))
                      .finally(() => setLoading(false));
                  }}
                >
                  <option value="" disabled>
                    A fresh conversation
                  </option>
                  {list.map((c) => (
                    <option key={c.id} value={c.id}>
                      {c.title}
                    </option>
                  ))}
                </select>
                <button
                  title="New conversation"
                  aria-label="New conversation"
                  disabled={busy}
                  onClick={() => void startNew()}
                >
                  <Plus size={17} />
                </button>
                <button
                  aria-label="Refresh conversation"
                  disabled={sending || !history}
                  onClick={() =>
                    history &&
                    void load(history.conversation.id).catch((e) =>
                      setError(e.message),
                    )
                  }
                >
                  <RefreshCw size={15} />
                </button>
                <button
                  aria-label="Delete conversation"
                  disabled={busy || !history}
                  onClick={() => void removeConversation()}
                >
                  <Trash2 size={15} />
                </button>
              </nav>
              <div
                className="chat-scroll"
                ref={scroll}
                onScroll={() => {
                  if (scroll.current)
                    autoScroll.current =
                      scroll.current.scrollHeight -
                        scroll.current.scrollTop -
                        scroll.current.clientHeight <
                      90;
                }}
              >
                {!history?.turns.length && (
                  <div className="chat-welcome">
                    <span className="chat-welcome-icon">
                      <Sparkles size={27} />
                    </span>
                    <div className="chat-kicker">A LITTLE HELP, RIGHT HERE</div>
                    <h3>
                      Good finds start
                      <br />
                      with a conversation.
                    </h3>
                    <p>
                      Tell me what you&apos;re looking for. We can find your
                      fit, check your bag, or follow an order together.
                    </p>
                    <div className="chat-starters">
                      {[
                        "Find Audio products under $100",
                        "What is in my bag?",
                        "Show my orders",
                      ].map((text) => (
                        <button
                          key={text}
                          disabled={busy}
                          onClick={() => void send(text)}
                        >
                          {text}
                          <ArrowUp size={14} />
                        </button>
                      ))}
                    </div>
                    <small>Your conversations are saved to your account.</small>
                  </div>
                )}
                {history?.turns.map((turn) => (
                  <div className="chat-turn" key={turn.id}>
                    <div className="chat-user-message">{turn.input}</div>
                    <div className="chat-assistant-label">
                      <Sparkles size={12} />
                      COMMERCEPILOT
                    </div>
                    {entries(
                      turn.id,
                      history.events,
                      history.cards,
                      turn.status,
                    ).map((item) =>
                      item.kind === "card" ? (
                        <ChatCardView
                          key={item.key}
                          card={item.card!}
                          busy={busy}
                          now={now}
                          send={(text) => void send(text)}
                            navigate={() => shop.setChatOpen(false)}
                          decide={(card, decision) =>
                            void decide(card, decision)
                          }
                        />
                      ) : item.kind === "progress" ? (
                        <div key={item.key} className="chat-progress">
                          {item.failed ? (
                            <AlertCircle size={12} />
                          ) : item.done ? (
                            <Check size={12} />
                          ) : (
                            <LoaderCircle size={12} className="spin" />
                          )}
                          {item.text}
                        </div>
                      ) : (
                        <div
                          key={item.key}
                          className={
                            item.kind === "error"
                              ? "chat-inline-error"
                              : "chat-assistant-message"
                          }
                        >
                          {item.text
                            ?.split(/(\*\*[^*]+\*\*)/g)
                            .map((part, i) =>
                              part.startsWith("**") && part.endsWith("**") ? (
                                <strong key={i}>{part.slice(2, -2)}</strong>
                              ) : (
                                part
                              ),
                            )}
                        </div>
                      ),
                    )}
                    {turn.status === "Interrupted" && (
                      <p className="chat-inline-error">
                        This reply was interrupted. Check the current bag or
                        review before repeating a change.
                      </p>
                    )}
                  </div>
                ))}
                {(sending || history?.conversation.activeTurnId) && (
                  <div className="chat-thinking" role="status">
                    <span />
                    <span />
                    <span />
                    Working on your request
                  </div>
                )}
                {loading && (
                  <p className="chat-progress">
                    <LoaderCircle className="spin" size={15} />
                    Loading your conversation
                  </p>
                )}
              </div>
              <div className="chat-review-shortcut">
                <button disabled={busy} onClick={() => void pendingReviews()}>
                  Pending reviews
                </button>
                <span>Prepared elsewhere? Find them here.</span>
              </div>
              {error && (
                <div className="chat-banner" role="alert">
                  {error}
                </div>
              )}
              <form className="chat-composer" onSubmit={submit}>
                <label className="sr-only" htmlFor="chat-message">
                  Message CommercePilot
                </label>
                <textarea
                  id="chat-message"
                  ref={textArea}
                  rows={2}
                  maxLength={2000}
                  placeholder="Find something good. Ask me anything about the store…"
                  value={input}
                  onChange={(e) => setInput(e.target.value)}
                  disabled={decisionBusy || loading}
                  onKeyDown={(e) => {
                    if (e.key === "Enter" && !e.shiftKey) {
                      e.preventDefault();
                      if (!busy) void send(input);
                    }
                  }}
                />
                {sending ? (
                  <button
                    type="button"
                    aria-label="Stop reply"
                    onClick={() => controller.current?.abort()}
                  >
                    <Square size={15} />
                  </button>
                ) : (
                  <button
                    type="submit"
                    aria-label="Send message"
                    disabled={busy || !input.trim()}
                  >
                    <ArrowUp size={20} />
                  </button>
                )}
                <span className="chat-composer-note">
                  Review cards show the exact terms. Your approval is always a
                  button.
                </span>
              </form>
            </>
          ) : (
            <div className="chat-welcome chat-signin">
              <MessageCircle size={36} strokeWidth={1.2} />
              <h3>
                A conversation
                <br />
                that stays with you.
              </h3>
              <p>
                Sign in to find products, manage your bag, and review orders in
                one place.
              </p>
              <button
                className="chat-primary"
                onClick={() => shop.setAccountOpen(true)}
              >
                Sign in to chat
              </button>
              <small>
                Your bag and conversations belong only to your account.
              </small>
            </div>
          )}
        </aside>
      )}
    </>
  );
}
