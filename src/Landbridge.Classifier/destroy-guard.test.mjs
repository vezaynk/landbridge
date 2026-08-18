import assert from "node:assert/strict";
import { test } from "node:test";
import { classify } from "./classify.mjs";
import { matchDestructiveCommand } from "./destroy-guard.mjs";

test("matches Qwen's git and IaC destroy list", () => {
  assert.ok(matchDestructiveCommand("git reset --hard HEAD"));
  assert.ok(matchDestructiveCommand("git checkout -- ."));
  assert.ok(matchDestructiveCommand("git clean -fd"));
  assert.ok(matchDestructiveCommand("git stash drop"));
  assert.ok(matchDestructiveCommand("git commit --amend --no-edit"));
  assert.ok(matchDestructiveCommand("terraform destroy -auto-approve"));
  assert.ok(matchDestructiveCommand("pulumi destroy"));
  assert.ok(matchDestructiveCommand("cdk destroy"));
  assert.ok(matchDestructiveCommand("bash -lc \"git reset --hard\""));
  assert.equal(matchDestructiveCommand("git status"), null);
  assert.equal(matchDestructiveCommand("npm test"), null);
});

test("destroy guard Asks and skips the LLM", async () => {
  let called = false;
  const r = await classify(
    { tool: "Bash", input: { command: "git reset --hard" } },
    {
      isReadOnly: async () => false,
      matchDestructive: matchDestructiveCommand,
      llm: async () => {
        called = true;
        return { disposition: "allow", via: "should-not-run" };
      },
    },
  );
  assert.equal(r.disposition, "ask");
  assert.equal(r.via, "destructive-command");
  assert.equal(called, false);
});
