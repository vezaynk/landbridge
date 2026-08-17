// Drives the ACP `initialize` handshake against a real agent binary and prints what it
// declares. This is the empirical answer to the loadSession question — the one capability
// that decides whether §11 resume survives the migration for a given harness.
import { spawn } from "node:child_process";

const TIMEOUT_MS = 45_000;

const INITIALIZE = {
  jsonrpc: "2.0",
  id: 0,
  method: "initialize",
  params: {
    protocolVersion: 1,
    clientCapabilities: {
      fs: { readTextFile: false, writeTextFile: false },
      terminal: false,
    },
    clientInfo: { name: "docketd-probe", version: "0" },
  },
};

async function probe(name, command, args) {
  const child = spawn(command, args, {
    stdio: ["pipe", "pipe", "pipe"],
    env: { ...process.env },
  });

  let out = "";
  let err = "";
  const messages = [];
  let resolved = null;

  const done = new Promise((resolve) => {
    resolved = resolve;
  });

  child.stdout.on("data", (chunk) => {
    out += chunk.toString();
    let idx;
    while ((idx = out.indexOf("\n")) >= 0) {
      const line = out.slice(0, idx).trim();
      out = out.slice(idx + 1);
      if (!line) continue;
      try {
        const msg = JSON.parse(line);
        messages.push(msg);
        if (msg.id === 0 && (msg.result || msg.error)) resolved(msg);
      } catch {
        messages.push({ __nonJson: line });
      }
    }
  });
  child.stderr.on("data", (c) => (err += c.toString()));
  child.on("error", (e) => resolved({ __spawnError: e.message }));
  child.on("exit", (code, signal) =>
    resolved({ __exited: { code, signal } }),
  );

  child.stdin.write(JSON.stringify(INITIALIZE) + "\n");

  const timer = setTimeout(() => resolved({ __timeout: true }), TIMEOUT_MS);
  const answer = await done;
  clearTimeout(timer);
  try { child.kill("SIGKILL"); } catch {}

  return { name, command, args, answer, messages, stderr: err.slice(0, 1200) };
}

const targets = [
  ["claude-agent-acp (current)", "node_modules/.bin/claude-agent-acp", []],
  ["claude-code-acp (deprecated)", "node_modules/.bin/claude-code-acp", []],
  ["codex-acp", "node_modules/.bin/codex-acp", []],
  ["opencode acp (native)", "node_modules/.bin/opencode", ["acp"]],
];

for (const [name, cmd, args] of targets) {
  const r = await probe(name, cmd, args);
  console.log("=".repeat(78));
  console.log(name, "→", cmd, args.join(" "));
  console.log("=".repeat(78));

  if (r.answer?.result) {
    const res = r.answer.result;
    console.log("protocolVersion :", res.protocolVersion);
    console.log("agentCapabilities:", JSON.stringify(res.agentCapabilities, null, 2));
    if (res.agentInfo) console.log("agentInfo       :", JSON.stringify(res.agentInfo));
    if (res.authMethods) console.log("authMethods     :", JSON.stringify(res.authMethods));
    console.log(">>> loadSession =", res.agentCapabilities?.loadSession === true);
    console.log(">>> mcp.http    =", res.agentCapabilities?.mcpCapabilities?.http === true);
  } else {
    console.log("NO INITIALIZE RESULT:", JSON.stringify(r.answer));
    if (r.messages.length)
      console.log("messages seen  :", JSON.stringify(r.messages.slice(0, 4), null, 2));
  }
  if (r.stderr.trim()) console.log("--- stderr ---\n" + r.stderr.trim());
  console.log();
}
