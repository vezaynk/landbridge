import http from "node:http";
import { classify } from "./classify.mjs";
import { matchDestructiveCommand } from "./destroy-guard.mjs";
import { makeLlmClassifier, readLlmConfig } from "./llm.mjs";
import { isShellCommandReadOnly } from "./vendor/qwen-readonly.mjs";

const PORT = Number.parseInt(process.env.PORT ?? "5310", 10);
const llm = makeLlmClassifier(readLlmConfig(process.env));

function send(res, status, body) {
  const json = JSON.stringify(body);
  res.writeHead(status, {
    "content-type": "application/json; charset=utf-8",
    "content-length": Buffer.byteLength(json),
  });
  res.end(json);
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    req.on("data", (chunk) => {
      size += chunk.length;
      if (size > 64 * 1024) {
        reject(new Error("body too large"));
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
    req.on("error", reject);
  });
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url ?? "/", "http://127.0.0.1");
  if (req.method === "GET" && url.pathname === "/health") {
    send(res, 200, { ok: true, llm: llm != null });
    return;
  }
  if (req.method !== "POST" || url.pathname !== "/classify") {
    send(res, 404, { error: "not found" });
    return;
  }

  let body;
  try {
    const raw = await readBody(req);
    body = raw ? JSON.parse(raw) : {};
  } catch {
    send(res, 200, { disposition: "ask", via: "bad-request", reason: "" });
    return;
  }

  if (!body || typeof body.tool !== "string" || body.tool.trim() === "") {
    send(res, 200, { disposition: "ask", via: "bad-request", reason: "" });
    return;
  }

  try {
    const result = await classify(
      { tool: body.tool, input: body.input ?? null },
      { isReadOnly: isShellCommandReadOnly, matchDestructive: matchDestructiveCommand, llm },
    );
    send(res, 200, result);
  } catch {
    send(res, 200, { disposition: "ask", via: "error", reason: "" });
  }
});

server.listen(PORT, "0.0.0.0", () => {
  process.stderr.write(
    `landbridge-classifier listening on :${PORT} llm=${llm != null}\n`,
  );
});
