import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { makeLlmClassifier, readLlmConfig, sanitizeReason } from "./llm.mjs";

test("readLlmConfig requires a key and a model", () => {
  assert.equal(readLlmConfig({}), null);
  assert.equal(readLlmConfig({ LANDBRIDGE_CLASSIFIER_API_KEY: "sk-test" }), null);
  assert.equal(readLlmConfig({ LANDBRIDGE_CLASSIFIER_MODEL: "gpt-4o-mini" }), null);
  assert.equal(
    readLlmConfig({
      LANDBRIDGE_CLASSIFIER_API_KEY: "   ",
      LANDBRIDGE_CLASSIFIER_MODEL: "gpt-4o-mini",
    }),
    null,
  );
  const cfg = readLlmConfig({
    LANDBRIDGE_CLASSIFIER_API_KEY: "sk-test",
    LANDBRIDGE_CLASSIFIER_MODEL: "gpt-4o-mini",
  });
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

test("server exits 1 without a key and a model", async () => {
  const env = { ...process.env, PORT: "0" };
  delete env.LANDBRIDGE_CLASSIFIER_API_KEY;
  delete env.LANDBRIDGE_CLASSIFIER_MODEL;
  const child = spawn(process.execPath, [fileURLToPath(new URL("./server.mjs", import.meta.url))], {
    env,
    stdio: ["ignore", "ignore", "pipe"],
  });
  const chunks = [];
  child.stderr.on("data", (c) => chunks.push(c));
  const code = await new Promise((resolve, reject) => {
    child.on("exit", resolve);
    child.on("error", reject);
  });
  assert.equal(code, 1);
  assert.match(Buffer.concat(chunks).toString(), /LANDBRIDGE_CLASSIFIER_API_KEY and LANDBRIDGE_CLASSIFIER_MODEL are required/);
});
