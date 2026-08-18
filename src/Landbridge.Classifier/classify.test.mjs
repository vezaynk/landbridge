import assert from "node:assert/strict";
import { test } from "node:test";
import { classify, extractCommand, lastSegment } from "./classify.mjs";

const allow = async () => true;
const deny = async () => false;
const boom = async () => {
  throw new Error("checker failed");
};

test("lastSegment strips harness prefixes", () => {
  assert.equal(lastSegment("mcp__landbridge__Bash"), "bash");
  assert.equal(lastSegment("run_shell_command"), "run_shell_command");
  assert.equal(lastSegment("tools/execute"), "execute");
});

test("extractCommand accepts string, object, and argv", () => {
  assert.equal(extractCommand('{"command":"git status"}'), "git status");
  assert.equal(extractCommand({ command: "ls -la" }), "ls -la");
  assert.equal(extractCommand({ command: ["git", "status"] }), "git status");
  assert.equal(extractCommand({ argv: ["git", "log", "--oneline"] }), "git log --oneline");
  assert.equal(extractCommand({}), null);
});

test("non-shell tools ask", async () => {
  const r = await classify({ tool: "Read", input: { path: "a.cs" } }, allow);
  assert.equal(r.disposition, "ask");
  assert.equal(r.via, "not-shell");
});

test("missing command asks", async () => {
  const r = await classify({ tool: "Bash", input: {} }, allow);
  assert.equal(r.disposition, "ask");
  assert.equal(r.via, "no-command");
});

test("readonly shell allows", async () => {
  const r = await classify({ tool: "Bash", input: { command: "git status" } }, allow);
  assert.equal(r.disposition, "allow");
  assert.equal(r.via, "readonly-shell");
});

test("non-readonly shell asks", async () => {
  const r = await classify({ tool: "execute", input: { command: "rm -rf /" } }, deny);
  assert.equal(r.disposition, "ask");
  assert.equal(r.via, "not-readonly");
});

test("bundled Qwen checker allows git status and asks on rm", async () => {
  const { isShellCommandReadOnly } = await import("./vendor/qwen-readonly.mjs");
  const allow = await classify(
    { tool: "Bash", input: { command: "git status" } },
    isShellCommandReadOnly,
  );
  const ask = await classify(
    { tool: "Bash", input: { command: "rm -rf /" } },
    isShellCommandReadOnly,
  );
  assert.equal(allow.disposition, "allow");
  assert.equal(ask.disposition, "ask");
});

test("checker throw asks, never denies", async () => {
  const r = await classify({ tool: "Bash", input: { command: "ls" } }, boom);
  assert.equal(r.disposition, "ask");
  assert.equal(r.via, "checker-error");
});
