/**
 * Stateless classification. If a command can be parsed and the Qwen
 * read-only checker allows it, that is the answer. Destroy-guard is Ask
 * with no model override. Everything else — including a title/rawInput we
 * could not parse as a command — goes to the two-stage LLM. Vacuous
 * execute (named shell, empty input, no command) is Ask, not a model call.
 * Unknown / error is Ask. Never Deny.
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
  "local_shell",
  "shell_command",
  "bash_tool",
  "run_command",
  "execute_command",
]);

const BARE_COMMANDS = new Set([
  "ls",
  "pwd",
  "git",
  "cat",
  "head",
  "tail",
  "wc",
  "echo",
  "date",
  "whoami",
  "uname",
  "which",
  "true",
  "false",
  "env",
  "id",
  "hostname",
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

/**
 * ACP permission titles are often the command itself (`git status`) or
 * `Execute \`…\``. Those are not tool names like Bash.
 */
export function commandFromToolTitle(tool) {
  if (typeof tool !== "string") return null;
  let s = tool.trim();
  if (!s) return null;
  const wrapped = /^(?:execute|run|shell)\s+`([\s\S]+)`\s*$/i.exec(s);
  if (wrapped) s = wrapped[1].trim();
  if (!s) return null;
  if (SHELL_TOOLS.has(lastSegment(s))) return null;
  if (/\s/.test(s)) return s;
  if (BARE_COMMANDS.has(s.toLowerCase())) return s;
  return null;
}

export function resolveCommand(tool, input) {
  return extractCommand(input) ?? commandFromToolTitle(tool);
}

export function isEmptyInput(input) {
  if (input == null) return true;
  if (typeof input === "string") {
    const t = input.trim();
    return t === "" || t === "{}" || t === "null";
  }
  if (typeof input === "object") {
    if (Array.isArray(input)) return input.length === 0;
    return Object.keys(input).length === 0;
  }
  return false;
}

function noCommandResult(isNamedShell) {
  return {
    disposition: "ask",
    via: isNamedShell ? "no-command" : "not-shell",
    reason: "",
  };
}

async function runLlm(llm, { tool, input, command }) {
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

export async function classify({ tool, input }, hooks = {}) {
  const isReadOnly = typeof hooks === "function" ? hooks : hooks.isReadOnly;
  const matchDestructive =
    typeof hooks === "function" ? undefined : hooks.matchDestructive;
  const llm = typeof hooks === "function" ? undefined : hooks.llm;
  const name = lastSegment(tool);
  const isNamedShell = SHELL_TOOLS.has(name);
  const command = resolveCommand(tool, input);

  if (command && typeof isReadOnly === "function") {
    let ok = false;
    try {
      ok = await isReadOnly(command);
    } catch {
      return { disposition: "ask", via: "checker-error", reason: "" };
    }
    if (ok) return { disposition: "allow", via: "readonly-shell", reason: "" };
  }

  if (command && typeof matchDestructive === "function") {
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
    if (!command && isEmptyInput(input)) return noCommandResult(isNamedShell);
    return runLlm(llm, { tool, input, command: command ?? null });
  }

  if (!command) return noCommandResult(isNamedShell);
  return { disposition: "ask", via: "not-readonly", reason: "" };
}
