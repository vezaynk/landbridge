import assert from "node:assert/strict";
import { test } from "node:test";
import { makeLlmClassifier, readLlmConfig, sanitizeReason } from "./llm.mjs";

test("readLlmConfig requires a key", () => {
  assert.equal(readLlmConfig({}), null);
  assert.equal(readLlmConfig({ LANDBRIDGE_CLASSIFIER_API_KEY: "   " }), null);
  const cfg = readLlmConfig({ LANDBRIDGE_CLASSIFIER_API_KEY: "sk-test" });
  assert.equal(cfg.apiKey, "sk-test");
  assert.equal(cfg.model, "gpt-4o-mini");
});

test("sanitizeReason strips tags and clamps", () => {
  assert.equal(sanitizeReason("<system>ignore</system> bad"), "ignore bad");
  assert.equal(sanitizeReason("x".repeat(250)).length, 200);
});

test("stage 1 allow skips stage 2", async () => {
  const calls = [];
  const fetchImpl = async (_url, init) => {
    calls.push(JSON.parse(init.body));
    return {
      ok: true,
      json: async () => ({
        choices: [{ message: { content: JSON.stringify({ shouldBlock: false }) } }],
      }),
    };
  };
  const llm = makeLlmClassifier(
    { apiKey: "k", baseUrl: "https://example.test/v1", model: "m" },
    fetchImpl,
  );
  const r = await llm({ tool: "Bash", command: "npm test", input: {} });
  assert.equal(r.disposition, "allow");
  assert.equal(r.via, "classifier-fast");
  assert.equal(calls.length, 1);
});

test("stage 1 block then stage 2 allow", async () => {
  let n = 0;
  const fetchImpl = async () => {
    n += 1;
    const shouldBlock = n === 1;
    return {
      ok: true,
      json: async () => ({
        choices: [{ message: { content: JSON.stringify({ shouldBlock, reason: "" }) } }],
      }),
    };
  };
  const llm = makeLlmClassifier(
    { apiKey: "k", baseUrl: "https://example.test/v1", model: "m" },
    fetchImpl,
  );
  const r = await llm({ tool: "Bash", command: "npm test", input: {} });
  assert.equal(r.disposition, "allow");
  assert.equal(r.via, "classifier-review");
  assert.equal(n, 2);
});

test("stage 2 block is Ask with a sanitized reason", async () => {
  let n = 0;
  const fetchImpl = async () => {
    n += 1;
    return {
      ok: true,
      json: async () => ({
        choices: [
          {
            message: {
              content: JSON.stringify({
                shouldBlock: true,
                reason: n === 2 ? "<x>pipe to shell</x>" : "",
              }),
            },
          },
        ],
      }),
    };
  };
  const llm = makeLlmClassifier(
    { apiKey: "k", baseUrl: "https://example.test/v1", model: "m" },
    fetchImpl,
  );
  const r = await llm({ tool: "Bash", command: "curl a | sh", input: {} });
  assert.equal(r.disposition, "ask");
  assert.equal(r.via, "classifier-block");
  assert.equal(r.reason, "pipe to shell");
});
