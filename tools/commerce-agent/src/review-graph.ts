import { Command, END, START, StateGraph, StateSchema, interrupt } from "@langchain/langgraph";
import { reviewIntent, reviewSchema, reviewThread, sameReview } from "./review-contract.js";
import type { Review, ReviewIntent, ReviewMemory } from "./review-contract.js";

const State = new StateSchema({ intent: reviewIntent, review: reviewSchema.nullable().default(null),
  outcome: reviewSchema.nullable().default(null) });
export type ReviewGateway = {
  prepare(intent: ReviewIntent, signal: AbortSignal): Promise<Review>;
  read(id: string, signal: AbortSignal): Promise<Review>;
};

export function createReviewFlow(memory: ReviewMemory, gateway: ReviewGateway, signal: AbortSignal) {
  const graph = new StateGraph(State)
    .addNode("prepare", async state => {
      const review = reviewSchema.parse(await gateway.prepare(state.intent, signal));
      if (review.kind !== state.intent.kind || review.status !== "Pending" ||
          (state.intent.kind !== "Checkout" && review.snapshot.orderId !== state.intent.orderId))
        throw new Error("Review preparation returned unexpected terms.");
      return { review, outcome: null };
    })
    .addNode("human_review", state => {
      // This node is replayed on resume. It has NO side effects and never calls a confirm endpoint.
      const resumed = interrupt({ type: "human_review", review: state.review });
      if (resumed !== "refresh") throw new Error("Review resume only accepts a status refresh.");
      return {};
    })
    .addNode("verify", async state => {
      const outcome = reviewSchema.parse(await gateway.read(state.review!.id, signal));
      if (!sameReview(state.review!, outcome)) throw new Error("Review terms changed unexpectedly.");
      return { outcome };
    })
    .addEdge(START, "prepare").addEdge("prepare", "human_review").addEdge("human_review", "verify")
    .addConditionalEdges("verify", state => state.outcome?.status === "Pending" ? "human_review" : END, ["human_review", END])
    .compile({ checkpointer: memory.checkpointer });
  const config = { configurable: { thread_id: reviewThread(memory.threadId) }, signal, recursionLimit: 8, durability: "sync" as const };
  return {
    async inspect() {
      const state = await graph.getState(config);
      return { review: state.values.review ? reviewSchema.parse(state.values.review) : undefined,
        outcome: state.values.outcome ? reviewSchema.parse(state.values.outcome) : undefined,
        waiting: state.next.length === 1 && state.next[0] === "human_review" && state.tasks.some(task => task.interrupts?.length),
        verifying: state.next.length === 1 && state.next[0] === "verify",
        unfinished: state.next.length > 0 };
    },
    async start(intent: ReviewIntent) {
      const state = await graph.getState(config);
      if (state.next.length) throw new Error("Review already pending or unfinished. Use /review or /sync-review.");
      await graph.invoke({ intent: reviewIntent.parse(intent), review: null, outcome: null }, config);
      return reviewSchema.parse((await graph.getState(config)).values.review);
    },
    async sync() {
      const state = await graph.getState(config);
      const verifying = state.next.length === 1 && state.next[0] === "verify";
      if (!verifying && !(state.next.length === 1 && state.next[0] === "human_review" && state.tasks.some(task => task.interrupts?.length)))
        throw new Error("Review is not waiting for human input.");
      // Even a forged resume payload cannot purchase: verify can only read authoritative API status.
      await graph.invoke(verifying ? null : new Command({ resume: "refresh" }), config);
      return reviewSchema.parse((await graph.getState(config)).values.outcome);
    },
  };
}

export type ReviewFlow = ReturnType<typeof createReviewFlow>;

export async function decideReview(flow: ReviewFlow, shown: Review, actionId: string, decision: "approve" | "reject",
  gateway: ReviewGateway & { decide(review: Review, decision: "approve" | "reject", signal: AbortSignal): Promise<Review> },
  claim: () => Promise<boolean>, signal: AbortSignal): Promise<{ status: "resolved" | "uncertain"; review?: Review }> {
  const state = await flow.inspect();
  if (!state.waiting || !state.review || actionId !== shown.id || !sameReview(state.review, shown))
    throw new Error("Review must be displayed in this session before a matching decision.");
  const current = await gateway.read(actionId, signal);
  if (!sameReview(shown, current)) throw new Error("Review terms changed. Display the current review.");
  if (current.status !== "Pending" || Date.parse(current.expiresAt) <= Date.now()) {
    return { status: "resolved", review: await flow.sync() };
  }
  // Durable reservation BEFORE HTTP. Lost replies or process death never authorize automatic resubmission.
  if (!await claim()) throw new Error("Review decision was already attempted. Use /sync-review; do not resend it.");
  try {
    await gateway.decide(shown, decision, signal); // Trusted command path only; not a graph/model tool.
    return { status: "resolved", review: await flow.sync() };
  } catch {
    // A confirm may have succeeded even when its response/checkpoint failed. Recovery is read-only.
    return { status: "uncertain" };
  }
}
