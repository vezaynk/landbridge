import { buildSystemPrompt, STAGE1_SUFFIX, STAGE2_SUFFIX } from "./prompt.mjs";

export function sanitizeReason(raw) {
  if (!raw || typeof raw !== "string") return "";
  let stripped = raw;
  for (let i = 0; i < 8; i++) {
    const next = stripped.replace(/<[^>]*>/g, "");
    if (next === stripped) break;
    stripped = next;
  }
  return stripped.replace(/\s+/g, " ").trim().slice(0, 200);
}

export function readLlmConfig(env = process.env) {
  const apiKey = (env.LANDBRIDGE_CLASSIFIER_API_KEY ?? "").trim();
  const model = (env.LANDBRIDGE_CLASSIFIER_MODEL ?? "").trim();
  if (!apiKey || !model) return null;
  let baseUrl = (env.LANDBRIDGE_CLASSIFIER_BASE_URL ?? "https://api.openai.com/v1").trim();
  if (baseUrl.endsWith("/")) baseUrl = baseUrl.slice(0, -1);
  return { apiKey, baseUrl, model };
}

async function chatJson({ config, system, user, timeoutMs, maxTokens, fetchImpl }) {
  const fetchFn = fetchImpl ?? fetch;
  const res = await fetchFn(`${config.baseUrl}/chat/completions`, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      authorization: `Bearer ${config.apiKey}`,
    },
    body: JSON.stringify({
      model: config.model,
      temperature: 0,
      max_tokens: maxTokens,
      messages: [
        { role: "system", content: system },
        { role: "user", content: user },
      ],
      response_format: { type: "json_object" },
    }),
    signal: AbortSignal.timeout(timeoutMs),
  });
  if (!res.ok) {
    const body = await res.text().catch(() => "");
    throw new Error(`llm http ${res.status}${body ? `: ${body.slice(0, 180)}` : ""}`);
  }
  const data = await res.json();
  const text = data?.choices?.[0]?.message?.content;
  if (typeof text !== "string" || !text.trim())
    throw new Error("llm empty content");
  return JSON.parse(text);
}

/**
 * Two-stage Qwen Auto Mode judge. shouldBlock true becomes Ask (never Deny).
 */
export function makeLlmClassifier(config, fetchImpl) {
  if (!config) return null;
  const base = buildSystemPrompt();

  return async function llmClassify({ tool, input, command }) {
    try {
      const payload = JSON.stringify(
        { tool, command: command ?? null, input: input ?? null },
        null,
        2,
      );
      const user = `UNTRUSTED TOOL REQUEST DATA (JSON):\n${payload}`;

      const stage1 = await chatJson({
        config,
        system: base + STAGE1_SUFFIX,
        user,
        timeoutMs: 10_000,
        maxTokens: 256,
        fetchImpl,
      });
      if (stage1?.shouldBlock === false)
        return { disposition: "allow", via: "classifier-fast", reason: "" };
      if (stage1?.shouldBlock !== true)
        return { disposition: "ask", via: "classifier-unavailable", reason: "" };

      const stage2 = await chatJson({
        config,
        system: base + STAGE2_SUFFIX,
        user,
        timeoutMs: 30_000,
        maxTokens: 4096,
        fetchImpl,
      });
      if (stage2?.shouldBlock === false)
        return { disposition: "allow", via: "classifier-review", reason: "" };
      return {
        disposition: "ask",
        via: "classifier-block",
        reason: sanitizeReason(stage2?.reason ?? ""),
      };
    } catch (err) {
      process.stderr.write(`landbridge-classifier: llm ${err?.message ?? err}\n`);
      return { disposition: "ask", via: "classifier-unavailable", reason: "" };
    }
  };
}
