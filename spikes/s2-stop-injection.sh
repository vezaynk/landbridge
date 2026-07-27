#!/usr/bin/env bash
# Spike S2 — stop delivered as an injected turn (spec §10, §17.0b)
# Proves: with --input-format stream-json, a queued user message carrying a
# stop disposition reaches a busy agent as its own turn, producing a graceful
# wind-down (persist + exit) instead of a signal-style abort.
set -euo pipefail

MODEL="${DOCKET_SPIKE_MODEL:-haiku}"
OUT="$(cd "$(dirname "$0")" && pwd)/out/s2"
rm -rf "$OUT" && mkdir -p "$OUT/work"
cd "$OUT/work"

user_msg() { # one stream-json user message on stdout
  python3 - "$1" <<'PY'
import json, sys
print(json.dumps({"type": "user", "message": {"role": "user",
  "content": [{"type": "text", "text": sys.argv[1]}]}}))
PY
}

TASK="You are a docket worker. Work through this slowly: create files step1.txt through step6.txt, one per turn, running 'sleep 3' between each. Do not create them all at once."
STOP="STOP (ttl: 60s, disposition: preserve). Wind down now: stop starting new steps, write a one-line summary of exactly which steps you completed to progress.txt, and end the session."

echo "== launching worker on a deliberately slow multi-step task, injecting stop after 20s"
{
  user_msg "$TASK"
  sleep 20
  echo "== injecting stop message" >&2
  user_msg "$STOP"
} | timeout 180 claude -p \
      --input-format stream-json --output-format stream-json --verbose \
      --model "$MODEL" --allowedTools "Bash,Write" \
      > "$OUT/events.jsonl" 2> "$OUT/stderr.log" || true

echo "== results"
STEPS_DONE=$(ls step*.txt 2>/dev/null | wc -l | tr -d ' ')
echo "   steps completed before stop: $STEPS_DONE / 6"

if [[ -f progress.txt ]]; then
  echo "   PASS: wind-down persisted progress.txt: $(cat progress.txt)"
else
  echo "   FAIL: no progress.txt — the stop did not produce a wind-down turn"
fi

if [[ "$STEPS_DONE" -lt 6 ]]; then
  echo "   PASS: stop interrupted the task before completion (disposition honored, not ignored)"
else
  echo "   AMBIGUOUS: all steps finished — task too fast; increase step count or sleep"
fi

echo "   event stream: $OUT/events.jsonl (verify the stop appears as a user turn, not a truncation)"
