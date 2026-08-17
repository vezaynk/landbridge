# Harness-CLI bump bot

Keeps the three BYO-harness CLI pins in `.github/workflows/ci.yml` from rotting, without letting
a bot spend money in a loop.

Once a day (`.github/workflows/harness-bump.yml`) this tool compares each pin against the
package's published `latest`, and when something is newer it opens a PR, dispatches ci.yml's
real-harness tiers against that PR's branch, and merges **only if those tiers actually passed**.

```sh
dotnet run --project tools/Docket.HarnessBump -- --dry-run   # report the plan, spend nothing
dotnet run --project tools/Docket.HarnessBump                # the real thing (needs GH_TOKEN)
```

`--dry-run` does every read — parse ci.yml, hit the registry, look for an open bump PR, decide —
and stops before creating or closing anything. It is the way to check what the bot would do today.
Outside GitHub Actions the mutating half additionally refuses to run without
`--allow-local-writes`, so forgetting `--dry-run` while debugging cannot push a branch and burn a
dispatch.

## Why this exists

The three tiers used to install `latest`. That is how `latest` moved mid-day on 2026-08-13: one
run installed `@anthropic-ai/claude-code@2.1.229` and went 9/9, another installed 2.1.231 and went
7/2 **on identical test code**. Because npm prints no version, the two versions had to be
reconstructed afterwards from registry publish times against step timestamps.

So the pins are exact now. The cost of exact pins is that they go stale silently, and this tool is
what pays it: drift still gets noticed daily, but it arrives as a PR that names the version in its
diff, gets verified by the real tiers before it lands, and never reddens somebody else's review.

## The algorithm, and the only piece of state

**The one open bump PR is the entire state.** No known-bad list, no tracking file, no label, no
memory of what has already been tried. Each run:

| situation | action |
|---|---|
| A bump PR is open and `latest` has climbed past what it targets | Close it as superseded, open a fresh PR at the new `latest` |
| A bump PR is open and it already targets `latest` | Nothing |
| No bump PR is open and a pin is behind `latest` | Open **one** combined PR for every available bump |

The cost guard falls out of that rule instead of being bolted on. A bump to version X gets exactly
**one** e2e run, when its PR is opened; every later run sees the open PR already targeting X and
idles. So a red bump sits open costing nothing rather than being retried daily. And because
nothing is remembered, a version that failed is retried as soon as a higher one ships — which a
suppression list would have refused to do.

What the PR targets is read back out of its **branch name** (`harness-bump/claude-2.1.232`,
`harness-bump/claude-2.1.232_opencode-1.18.19`). That is a label on live state, not a history:
nothing reads the branch names of closed or merged PRs.

Superseding **closes and reopens** rather than force-pushing the existing PR, so each version keeps
its own PR and its own e2e record. The close happens *before* the new PR is opened, so the
single-open-PR invariant is never briefly violated — a run that dies between the two steps leaves
no open PR, and the next run simply opens the right one.

Two bump PRs open at once means something other than the bot created one. The bot **refuses to
act** and exits non-zero, rather than closing a human's work or double-spending.

## Three decisions worth knowing

**It reads `dist-tags.latest`, never the newest version by publish time.** Both other harnesses
would break under a newest-by-timestamp rule: `@openai/codex` publishes `0.148.0-alpha.12` while
`latest` is `0.147.0`, and `opencode-ai` publishes `0.0.0-dev-<stamp>` snapshots that are the most
recent thing on the registry and rank *below* `1.18.18`. `SemVer` implements the prerelease rules
and the planner refuses any prerelease target, as second and third lines of defence.

**It polls the dispatch rather than using GitHub's auto-merge.** Auto-merge lands a PR when its
required checks pass, and the real-harness jobs can never be among them — they are
`if: github.event_name == 'workflow_dispatch'`, so on a `pull_request` event they do not run.
Auto-merge would therefore merge the bump the moment `build-test` and `chaos` went green, which is
exactly the evidence that says nothing about whether the new CLI works.

**A green job is not enough.** The real-harness facts are `SkippableFact`s that skip when their
API-key secret is absent, and the job still concludes `success` — by design, so fork PRs pass. The
bot therefore reads each job's log and requires that facts actually *passed*; a tier that went
green having skipped everything counts as **not verified** and blocks the merge.

### The log fetch is gh-version-coupled — do not "simplify" it

Reading those logs is the step that broke the first real run, and it is worth knowing why before
touching it. Job logs carry terminal escape sequences in their coloured test lines (the log for run
31829608810's real-claude job has 8 ESC bytes; its xUnit summary line is plain). **Newer gh refuses
to emit such a response** without `--allow-escape-sequences`, and **gh 2.92.0 rejects that flag
outright** with `unknown flag`. Either choice alone breaks the other version — and because this gate
fails closed, the wrong choice does not error loudly, it makes the bot **silently stop merging
anything**. So `ReadJobLogAsync` passes the flag first and retries without it on exactly an
`unknown flag` rejection. The escape-sequence refusal deliberately does *not* trigger that retry,
since retrying without the flag is precisely what cannot work in that case.

`JobLogArgs` is a pure function so both argv forms are asserted in tests; the first real run's
failure was invisible to the original tests because they fed log *text* to the parser and never
exercised the fetch that produces it.

When the fetch fails, the PR comment says the log **could not be read** and quotes gh's error —
distinct from "read it and found no summary". The first occurrence of this required someone to
hand-audit an entirely green run to discover the bot was fine and the reader was not.

## Adding another harness

An npm-packaged CLI needs an entry in `HarnessPackages.All`, a pinned `npm install -g` line in
ci.yml's `real-e2e` matrix, and a `harness:` include row. A non-npm CLI (Grok's `install.sh`,
Goose's `download_cli.sh`) is not a pin this bot can move — leave it out of
`HarnessPackages.All`. Either way the display name
`real-<harness>-e2e` goes in `E2eVerifier.RealHarnessJobs`: a test fails if the matrix gains a
cell the merge gate does not wait on. The tool tracks only pinned installs: an unpinned one is
reported, not bumped, because choosing the first known-good version to pin at is a judgement that
belongs in a human's PR.

## Exit codes

`0` nothing to do, or bumped and merged. `1` a bump was proposed but did not verify — the PR is
open for triage. `2` the tool could not run, or refused to act (bad arguments, unreadable ci.yml,
two open bump PRs, an unrecognisable bump branch, local run without `--allow-local-writes`).
