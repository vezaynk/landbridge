/**
 * Stateless classification. First cut is the Qwen read-only shell checker
 * only — no model. Unknown / unparsed / non-shell is Ask. Never Deny.
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

export async function classify({ tool, input }, isReadOnly) {
  const name = lastSegment(tool);
  if (!SHELL_TOOLS.has(name)) {
    return { disposition: "ask", via: "not-shell", reason: "" };
  }
  const command = extractCommand(input);
  if (!command) {
    return { disposition: "ask", via: "no-command", reason: "" };
  }
  let ok = false;
  try {
    ok = await isReadOnly(command);
  } catch {
    return { disposition: "ask", via: "checker-error", reason: "" };
  }
  if (ok) return { disposition: "allow", via: "readonly-shell", reason: "" };
  return { disposition: "ask", via: "not-readonly", reason: "" };
}
