#!/usr/bin/env bash
# Spike S3 — per-tool-call events with task attribution (spec §10, §17.0c)
# Proves: PostToolUse hooks fire per tool call, inherit DOCKET_TASK_ID from
# the spawning environment, and can report {task id, tool name} — the event
# source docketd's per-task liveness derives from.
set -euo pipefail

MODEL="${DOCKET_SPIKE_MODEL:-haiku}"
OUT="$(cd "$(dirname "$0")" && pwd)/out/s3"
rm -rf "$OUT" && mkdir -p "$OUT/work"
EVENTS="$OUT/events.log"

cat > "$OUT/hook.sh" <<HOOK
#!/usr/bin/env bash
# Receives hook JSON on stdin; docketd's real handler would POST to loopback.
tool=\$(python3 -c 'import json,sys; print(json.load(sys.stdin).get("tool_name","?"))')
echo "\${DOCKET_TASK_ID:-MISSING} \$tool" >> "$EVENTS"
HOOK
chmod +x "$OUT/hook.sh"

cat > "$OUT/settings.json" <<SETTINGS
{
  "hooks": {
    "PostToolUse": [
      { "hooks": [ { "type": "command", "command": "$OUT/hook.sh" } ] }
    ]
  }
}
SETTINGS

echo "== running a worker that must make several tool calls (task id: spike-task-42)"
cd "$OUT/work"
DOCKET_TASK_ID="spike-task-42" DOCKET_MACHINE_ID="spike-machine" \
  timeout 180 claude -p \
    "Create hello.txt containing 'hi', then run 'wc -c hello.txt', then run 'ls'. Use one tool call per action." \
    --model "$MODEL" --settings "$OUT/settings.json" --allowedTools "Bash,Write" \
    > "$OUT/result.txt" 2> "$OUT/stderr.log" || true

echo "== results"
if [[ ! -s "$EVENTS" ]]; then
  echo "   FAIL: no hook events captured"
  exit 1
fi

TOTAL=$(wc -l < "$EVENTS" | tr -d ' ')
ATTRIBUTED=$(grep -c '^spike-task-42 ' "$EVENTS" || true)
echo "   events: $TOTAL, correctly attributed: $ATTRIBUTED"
sed 's/^/     /' "$EVENTS"

if [[ "$TOTAL" -ge 3 && "$ATTRIBUTED" -eq "$TOTAL" ]]; then
  echo "   PASS: every tool call produced an event carrying the task id"
else
  echo "   FAIL: missing events or attribution — see $EVENTS"
fi
