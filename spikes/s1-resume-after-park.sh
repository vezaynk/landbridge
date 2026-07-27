#!/usr/bin/env bash
# Spike S1 — resume-after-park (spec §11, §17.0a)
# Proves: headless resume works with a new prompt, retains session context,
# and is scoped to the directory that created the session.
set -euo pipefail

MODEL="${DOCKET_SPIKE_MODEL:-haiku}"
OUT="$(cd "$(dirname "$0")" && pwd)/out/s1"
rm -rf "$OUT" && mkdir -p "$OUT/dir-a" "$OUT/dir-b"

echo "== phase 1: create a session in dir-a with a memorable fact"
CODEWORD="PELICAN-7"
SESSION_JSON="$(cd "$OUT/dir-a" && claude -p \
  "Remember this codeword and confirm you have it: $CODEWORD. Reply with one short sentence." \
  --model "$MODEL" --output-format json)"
echo "$SESSION_JSON" > "$OUT/create.json"
SESSION_ID="$(echo "$SESSION_JSON" | python3 -c 'import json,sys; print(json.load(sys.stdin)["session_id"])')"
echo "   session: $SESSION_ID"

echo "== phase 2: resume from dir-a (the park record's directory)"
RESUME_A="$(cd "$OUT/dir-a" && claude -p --resume "$SESSION_ID" \
  "What was the codeword? Reply with the codeword only." \
  --model "$MODEL" --output-format json)"
echo "$RESUME_A" > "$OUT/resume-a.json"
ANSWER="$(echo "$RESUME_A" | python3 -c 'import json,sys; print(json.load(sys.stdin)["result"])')"

if echo "$ANSWER" | grep -q "$CODEWORD"; then
  echo "   PASS: resumed session recalled '$CODEWORD'"
else
  echo "   FAIL: resumed session answered: $ANSWER"
fi

echo "== phase 3: attempt resume from dir-b (a different machine, effectively)"
if (cd "$OUT/dir-b" && claude -p --resume "$SESSION_ID" "What was the codeword?" \
    --model "$MODEL" --output-format json) > "$OUT/resume-b.json" 2>&1; then
  echo "   UNEXPECTED: resume succeeded outside the creating directory — spec §11 assumption is WRONG, re-check"
else
  echo "   PASS (expected failure): session lookup is directory-scoped"
  echo "   error was: $(tail -c 300 "$OUT/resume-b.json")"
fi

echo "== done. Evidence in $OUT — record versions and flags in FINDINGS.md"
