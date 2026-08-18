/**
 * Qwen Auto Mode Layer 0 destroy guard (Apache-2.0, QwenLM/qwen-code
 * packages/core/src/permissions/destructive-commands.ts, b4f5079).
 *
 * Qwen hard-blocks these unless the last user prompt clearly asked to discard
 * or destroy. Landbridge never AutoDenies: a match is Ask, and we do not take
 * a user-prompt override (the classify request does not carry that text).
 */

const DESTRUCTIVE_GIT = Object.freeze([
  /\bgit\s+reset\s+--hard\b/,
  /\bgit\s+checkout\s+--\s+\./,
  /\bgit\s+clean\s+-[a-zA-Z]*f/,
  /\bgit\s+stash\s+drop\b/,
]);

const GIT_AMEND = /\bgit\s+commit\s+--amend\b/;

const IAC_DESTROY = Object.freeze([
  /\bterraform\s+destroy\b/,
  /\bpulumi\s+destroy\b/,
  /\bcdk\s+destroy\b/,
]);

function stripShellQuotes(command) {
  return command.replace(
    /(?:^|\s)(?:bash|sh|zsh|fish|dash|ksh)\s+-[a-zA-Z]*c\s+(?:"([^"]*)"|'([^']*)')/g,
    (_match, dq, sq) => " " + (dq ?? sq ?? ""),
  );
}

/**
 * @returns {{ blocked: true, reason: string } | null}
 */
export function matchDestructiveCommand(command) {
  if (typeof command !== "string" || !command.trim()) return null;
  const expanded = command + " " + stripShellQuotes(command);

  for (const pattern of DESTRUCTIVE_GIT) {
    if (pattern.test(expanded)) {
      const matched = command.match(pattern)?.[0] ?? command;
      return {
        blocked: true,
        reason: `Destructive git command: "${matched}".`,
      };
    }
  }

  if (GIT_AMEND.test(expanded)) {
    return {
      blocked: true,
      reason: 'Blocked "git commit --amend" (not known to be this session\'s commit).',
    };
  }

  for (const pattern of IAC_DESTROY) {
    if (pattern.test(expanded)) {
      const toolName = command.match(pattern)?.[0]?.split(/\s+/)[0] ?? "unknown";
      return {
        blocked: true,
        reason: `Infrastructure destroy command: "${toolName} destroy".`,
      };
    }
  }

  return null;
}
