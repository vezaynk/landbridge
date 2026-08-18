/**
 * Stateless classification. Read-only shell first, Qwen destroy-guard next,
 * two-stage LLM last. Unknown / error is Ask. Never Deny. The HTTP server
 * refuses to start without a model and API key; tests may omit the LLM.
 */

const SHELL_TOOLS = new Set([
  "bash",
  "shell",
  "execute",
  "run_shell_command",
  "terminal",
  "cmd",
  "powershell",
  "sh",
  "zsh",
]);

export function lastSegment(tool) {
  if (typeof tool !== "string") return "";
  let s = tool.trim();
  const dunder = s.lastIndexOf("__");
  if (dunder >= 0) s = s.slice(dunder + 2);
  else {
    const slash = s.lastIndexOf("/");
    if (slash >= 0) s = s.slice(slash + 1);
  }
  return s.replace(/[- ]/g, "_").toLowerCase();
}

export function parseInput(input) {
  if (input == null) return null;
  if (typeof input === "object") return input;
  if (typeof input !== "string") return null;
  const trimmed = input.trim();
  if (!trimmed) return null;
  try {
    return JSON.parse(trimmed);
  } catch {
    return { command: trimmed };
  }
}

function shellQuote(token) {
  if (/^[A-Za-z0-9_./:@%+=,-]+$/.test(token)) return token;
  return `'${token.replace(/'/g, `'\\''`)}'`;
}

export function extractCommand(input) {
  const obj = parseInput(input);
  if (obj == null) return null;
  if (typeof obj === "string") return obj;
  const cmd = obj.command ?? obj.cmd ?? obj.argv;
  if (typeof cmd === "string") return cmd;
  if (Array.isArray(cmd) && cmd.length > 0 && cmd.every((x) => typeof x === "string"))
    return cmd.map(shellQuote).join(" ");
  return null;
}

export async function classify({ tool, input }, hooks = {}) {
  const isReadOnly = typeof hooks === "function" ? hooks : hooks.isReadOnly;
  const matchDestructive =
    typeof hooks === "function" ? undefined : hooks.matchDestructive;
  const llm = typeof hooks === "function" ? undefined : hooks.llm;
  const name = lastSegment(tool);
  const isShell = SHELL_TOOLS.has(name);
  const command = extractCommand(input);

  if (isShell && command && typeof isReadOnly === "function") {
    let ok = false;
    try {
      ok = await isReadOnly(command);
    } catch {
      return { disposition: "ask", via: "checker-error", reason: "" };
    }
    if (ok) return { disposition: "allow", via: "readonly-shell", reason: "" };
  } else if (isShell && !command) {
    return { disposition: "ask", via: "no-command", reason: "" };
  }

  if (isShell && command && typeof matchDestructive === "function") {
    try {
      const hit = matchDestructive(command);
      if (hit?.blocked) {
        return {
          disposition: "ask",
          via: "destructive-command",
          reason: hit.reason ?? "",
        };
      }
    } catch {
      return { disposition: "ask", via: "checker-error", reason: "" };
    }
  }

  if (typeof llm === "function") {
    try {
      const r = await llm({ tool, input, command });
      if (r && r.disposition === "allow")
        return {
          disposition: "allow",
          via: r.via ?? "classifier",
          reason: r.reason ?? "",
        };
      return {
        disposition: "ask",
        via: r?.via ?? "classifier-block",
        reason: r?.reason ?? "",
      };
    } catch {
      return { disposition: "ask", via: "classifier-unavailable", reason: "" };
    }
  }

  if (!isShell) return { disposition: "ask", via: "not-shell", reason: "" };
  return { disposition: "ask", via: "not-readonly", reason: "" };
}
