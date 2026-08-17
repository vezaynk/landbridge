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

**The requeue cap (§9 check 7).** *Decided, 2026-08-16:* there is no automatic requeue.
Infrastructure giving up (`LivenessLost`) parks the attempt as `Failed` with a
plane-authored reason. The counter is observability, not a loop. Resume is the Lead's
(`WakeParked` / `answer_input_request` with a note → `session/load`). A fail verdict is
`Rejected`, not a retry.

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

**Concurrency.** *Decided:* do not prescribe `max_concurrent`. Back-pressure is what
`docketd` observes (memory/disk/load). Waiting sessions may occupy seats; `park_task` is
how a Lead frees one.

**Token lifetime.** *Decided:* the token lives with the instance. Revoke on park, fail,
cancel, kill, or process exit. A session held idle still holds its credential; that is
deliberate and ends when the Lead parks or the process dies.

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

**Stage 3 — fail is a park, a report is not a yield.** *Implemented on the task model,
2026-08-16 — the domain is not renamed yet.* The decisions that were blocking the
rename, recorded here so the rename does not reopen them:

- **`Failed` is a park the Lead did not ask for.** Handshake flake, process death, silent
  machine, turn ended with no report: token revoked, process gone, workspace kept,
  plane-authored reason, inbox. Not terminal. No auto-requeue.
- **Resume is `WakeParked` with a note** (`answer_input_request` → `session/load`). The
  Lead decides whether the reason was flaky.
- **A report keeps the process.** `working → verifying` does not revoke the token or
  clear services. The Lead accepts (`Completed`), replies (`LeadMessage` → `working`),
  parks, or fails (`Rejected` — no retry loop).
- **Review mode trusts the Lead.** The plane will not refuse a Lead accept. Escalate
  when the evidence is a person's to own.
- **New work is a new session.** A rejected assignment is done. More from the same
  worker is a reply, not a fail-and-redispatch.
- **ACP client offers protocol 1 only.** Hand-rolled client stays; do not advertise v2.
- **`session/load` is machine-local** (#175). Work-dir GC is skill advice. Spend is
  metered only — the operator's own provisioning is the cap.

The domain rename (session states, migrations, dashboard reoriented around conversation)
is still ahead of this file's original stage-3 sentence. The engine now behaves as if
the session were the record.

**Stage 4 — the Lead can talk back.** *Implemented on the task model.* A fail is
`Rejected`, not a continuation. More from the same worker is `LeadMessage` /
`answer_input_request`: the plane doorbells; the worker pulls the text on
`get_task`. A pending permission still needs a verdict, not prose.
Continuations remain for cleanup and for *new* work that should inherit a
transcript — not for retrying a rejected assignment.

**Stage 5 — recovery.** *Implemented, same-machine only.* Park and fail pin
`PreferredMachine` to the box that held the session (`OnMachineGone = Pin`).
Wake is `WakeParked` with a note; dispatch carries `ResumeSessionRef`; the
runner `session/load`s in the original cwd. If that machine is gone the task
waits in `submitted`. Moving the session to another box is #175.

## Open questions

- **Does a session span more than one piece of work?** *Decided:* no. New work is a new
  session. A Lead reply on a live report is the same assignment continuing, not a second
  piece of work. A fail or accept ends it.
- **What closes a session?** `park_task` / accept / fail / cancel: `session/cancel`, token
  revoked. Later wake of a park or fail is `session/load`. An idle session you will not
  speak to again is a leak — park it.
- **Does the §7 profile still describe how to *launch*?** Under sessions it increasingly
  describes how to *reach* — which is a different thing, and may want a different key than
  `spawn`.
- **Cost.** *Decided:* Docket meters, it does not cap. `PromptResponse.usage` still
  feeds the §12 measured view. Spend limits belong to the operator's own
  provisioning (provider key ceilings, billing alerts, how many machines they
  enroll). There is no plane dollar ceiling and no `--max-turns` successor.
