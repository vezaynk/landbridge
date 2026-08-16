# Sessions

A design note for the move from **tasks** to **sessions** as Docket's primary domain object.
Not a spec change yet — §6, §7 and §11 still describe the task model, and they stay
authoritative until the ladder at the bottom of this file has actually been climbed.

## The decision

Three choices were made explicitly on 2026-08-15:

1. **Sessions replace tasks in the domain model.** Not "sessions as a mechanism, tasks as the
   ledger" — the conversation becomes the primary object, and §6/§7/§11 reorient around it.
2. **A worker that asks a question ends its turn; its session stays alive.** The answer
   arrives as a fresh `session/prompt` on the same connection, not as a redispatch.
3. **A session is held indefinitely**, not evicted on a TTL. If the process dies anyway, the
   next invocation resumes it from the transcript via `session/load`. **Implemented:**
   `Docket:WaitTtl` defaults to infinite; the sweeper still requeues a dead machine.
   `park_task` is the deliberate release.

The enabling change is already in: every worker is an ACP peer (see
[`runner-config.md`](skills/references/runner-config.md)) rather than a process whose only
observable lifecycle event is its own exit. Every measured agent declares
`loadSession: true`, which is what makes choice 3's recovery real rather than
aspirational.

## What actually changes

The task model fuses three things that are separate under ACP:

| | Task model | Session model |
|---|---|---|
| unit of work | task | session |
| unit of conversation | one turn | many turns |
| unit of process | one spawn per turn | one process per session |
| a question | park → redispatch → cold start or replay | a turn ends; next message continues |
| a rejection | a new continuation task | a message on the same session |
| the record | task row + a transcript on a machine | the conversation is the record |

The single sentence version: **a worker stops being something you launch and becomes
something you talk to.**

### "Hold indefinitely" still needs durable state

Worth stating early because it is the most likely thing to be got wrong. Choosing not to
evict is not the same as choosing not to persist. If a machine dies while a session sits
waiting on a human, something has to know that session existed, what it was for, and how to
bring it back — otherwise "resume from transcript on next invocation" has no invocation to
hang off, and the work is simply lost.

So the plane still records, durably: the session's harness ref, its workspace and work dir,
its profile and machine, and its message history. What goes away is the *TTL sweeper that
proactively converts a live session into a parked row* — not the row.
That sweeper is now off by default. `park_task` is how a Lead frees the machine.

`session/load` also constrains recovery: the spec requires the same `cwd` and `mcpServers`
as the original `session/new`. Work dirs already persist, so this is satisfiable, but it
means **the work dir is part of the session's identity** and can no longer be treated as
scratch that any cleanup may reclaim.

## The pull stays: an MCP read is a read receipt

**A constraint on every stage below, and the one most easily lost by accident.** The session
model makes it cheap to *push* a message at a worker. It must not, because the push is not
the delivery — the worker's own `get_task` is.

Today the answer to an input request waits on the assignment and reaches the worker on its
next `get_task` (`WorkerAssignment`, §11). Three properties ride on that being a pull:

1. **It is a receipt.** A pulled answer is one the worker demonstrably received, because it
   asked for it. A pushed one is *delivered to a queue* — the same gap between "written" and
   "read" that `StopDelivery.MessageWritten` exists to be honest about, and which made
   `Delivery == Message` a false positive for every `claude -p` worker. The plane must be
   able to tell a worker that acted on an answer from one that never saw it.
2. **It is confidential.** Answer content deliberately never travels as spawn argv, which is
   world-readable through `ps` and `/proc/<pid>/cmdline` — the same reason §13 keeps
   enrollment tokens out of argv. A live session must not become a second path out of the
   authenticated MCP channel.
3. **Config stays config.** The turn text has to name the docket tools the way *this* harness
   spells them (`mcp__docket__get_task` / `docket_get_task` / `docket__get_task`), so it is
   profile configuration. Per-message content in a profile-shaped turn mixes the two.

So the §10 `prompt` command carries **no message**: it names a task, the runner sends that
profile's `follow_up` turn ("there is new input, go read it"), and the worker pulls. The
session is what makes the wake-up cheap — no respawn, no cold start, no replay — not what
carries the payload. Stage 4's Lead-to-worker messages take the same route.

One thing this does *not* yet do: the receipt is **inferable but not recorded**.
`GetAssignmentAsync` is a pure read and stamps nothing, so today the plane knows an answer
was fetched only in the sense that a worker which acted on it must have fetched it. Making
the receipt observable — a read timestamp on the assignment, so an unanswered-but-woken
session is distinguishable from an unwoken one — belongs to stage 3, where the session
becomes the record.

## Properties that must be re-derived, not dropped

The task model carries correctness properties that are currently expressed in terms of
tasks. Each needs a home on the new primitive. This list is the actual risk of the
migration — the plumbing is easy by comparison.

**The doer/judge split (§9 check 4).** Today: a task's own worker can never complete it;
completion is a Lead-session or human verdict, and provenance is recorded. Under a session
model where the Lead and the worker exchange messages *on the same session*, "who is
speaking" stops being implied by the credential that opened the object. The split has to
become explicit — a message carries an authorship class, and a verdict is only accepted from
a Lead or human author. Spec §2's note that this "holds structurally regardless of who is
watching" is exactly what must not weaken.

**The requeue cap (§9 check 7).** Today the infrastructure counter counts *dispatches*. A
session that never redispatches has no natural counter, so a wedged session could be
nursed forever by follow-up messages. The cap needs re-expressing — most likely as
recoveries (process deaths the plane recovered from), which is the same thing the counter
was really measuring.

**Liveness.** Today: process-alive plus a progress clock, with liveness suspended while
blocked. A held-idle session is a third state the clocks have never had to represent —
process alive, no progress expected, and that is *correct* rather than a symptom. The
existing `InputRequestKind.Permission` live wait is the one precedent in the codebase.

*Partly done, and the reason it could not wait for stage 3.* A worker that ends its turn
still in `working` — reporting nothing and asking nothing — is invisible to both clocks: the
process is up and heartbeating, so neither ever fires. Under the task model that same
silence arrived as a process death and requeued. Stage 1's `turn-ended` is now read as its
successor (`LivenessLossReason.TurnEndedWithoutResult`), because without it the first real
ACP dispatch to go quiet simply hung — measured 2026-08-16, twice, once per agent. That is
the *fourth* state named, not the third: a turn ended in `working` is a fault, a session
held idle awaiting input is not, and only the first requeues.

**Concurrency.** `max_concurrent` counts dispatched tasks. Under indefinite holding, a fleet
where many sessions await humans consumes every seat while doing nothing. Sessions awaiting
input must either not count, or count against a separate ceiling.

**Token lifetime.** The worker-instance token dies with the instance (§9 check 14), and the
park path explicitly revokes it. A session held across a long wait keeps its credential live
for that whole period, which is a real change in exposure and should be a deliberate one.

## Staged ladder

Each stage is shippable and the system works at the end of it. The ordering is chosen so the
state machine — 175 core tests and the properties above — is touched last, once the
mechanism underneath it is known to work.

**Stage 1 — a session that outlives a turn.** *Runner only, no plane changes.* `AcpClient`
goes idle after `session/prompt` returns instead of finishing, and accepts follow-up prompts
on the live connection. Adds two members to the §10 vocabulary: a command to deliver a
follow-up prompt, and a turn-ended event carrying `stopReason` (information the task model
never had — today a token-limit stop, a refusal and a clean finish are indistinguishable).
Closes the session only on stop/kill.

**Stage 2 — a question stops suspending.** `request_input` no longer parks: the session goes
idle-awaiting-input and the Lead's answer is delivered as a follow-up prompt. The wait-TTL
sweeper is recovery-only (implemented: TTL off by default; machine-death still requeues).
`park_task` closes a session on purpose. Liveness grows its third state.
**Implemented:** a question stays `working` (permission is still the one
`blocked_on_input` live wait). `answer_input_request` on a live process is
`ContinueSession` or `LeadMessage` + `PromptCommand`; a gone process still
redispatches. ACP `session/request_permission` routes through
`POST /worker/permission` onto the existing permission tools. A turn that
ends while a question is pending is idle, not `TurnEndedWithoutResult`.

**Stage 3 — the session becomes the record.** Message history persisted; session states
replace task states; migrations; dashboard reoriented. **This is where the properties above
get re-derived**, and it is the stage that deserves its own design pass.

**Stage 4 — the Lead can talk back.** A rejection becomes a message rather than a
continuation task. Requires the doer/judge authorship rule from stage 3 to already hold.
**Started:** `LeadMessage` is working → working on a live session with no
pending question. The plane doorbells; the worker pulls the text on
`get_task`. A pending permission still needs a verdict. Full
replacement of continuation tasks waits on stage 3's authorship rule.

**Stage 5 — recovery.** A dead session is resumed from its persisted ref via `session/load`
on next invocation, replacing the park/redispatch path entirely.

## Open questions

- **Does a session span more than one piece of work?** If a Lead can keep talking to a
  worker, the boundary that made a task a unit of accountability becomes a convention rather
  than a mechanism. Something has to say when a session is *done* — otherwise long-lived
  sessions accumulate context until they hit the window and get worse at their jobs.
- **What closes a session?** `session/close` is declared by every measured agent. Completion
  is the obvious trigger; cancellation is the other. An idle session that will never be
  spoken to again is `park_task`: `session/cancel`, token revoked, later wake is
  `session/load`.
- **Does the §7 profile still describe how to *launch*?** Under sessions it increasingly
  describes how to *reach* — which is a different thing, and may want a different key than
  `spawn`.
- **Cost.** The migration gave up `--max-turns` (see runner-config.md). It did *not*, in the
  end, give up token accounting — `PromptResponse.usage` carries the four disjoint buckets
  after all, measured 2026-08-16 — so the meter survives and only the cap is gone.
  Indefinite sessions with unbounded follow-ups remove the last implicit bound, which was
  "a dispatch eventually exits". Something has to replace it.
