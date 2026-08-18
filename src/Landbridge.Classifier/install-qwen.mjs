/**
 * Rebuild vendor/qwen-readonly.mjs from Qwen Code main (Apache-2.0).
 * Published npm @qwen-code/qwen-code-core does not ship this checker.
 */
import { execFileSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const sha = process.env.LANDBRIDGE_QWEN_SHA ?? "b4f5079dc0cc89ce1448785898ab72b358399c95";
const work = process.env.LANDBRIDGE_QWEN_WORK ?? "/tmp/qwen-code";
const srcDir = join(here, "vendor", "qwen-src");

function run(cmd, args, cwd) {
  execFileSync(cmd, args, { cwd, stdio: "inherit" });
}

if (!existsSync(join(work, "packages", "core", "src", "utils", "shellReadOnlyChecker.ts"))) {
  rmSync(work, { recursive: true, force: true });
  run("git", [
    "clone",
    "--filter=blob:none",
    "--sparse",
    "https://github.com/QwenLM/qwen-code.git",
    work,
  ]);
  run("git", ["sparse-checkout", "set", "packages/core/src/utils"], work);
}
run("git", ["fetch", "--depth", "1", "origin", sha], work);
run("git", ["checkout", sha], work);

rmSync(srcDir, { recursive: true, force: true });
mkdirSync(srcDir, { recursive: true });
for (const name of ["shellReadOnlyChecker.ts", "shell-safety-rules.ts", "shell-utils.ts"]) {
  cpSync(join(work, "packages", "core", "src", "utils", name), join(srcDir, name));
}

const utils = join(srcDir, "shell-utils.ts");
let text = readFileSync(utils, "utf8");
text = text.replace("import type { AnyToolInvocation } from '../index.js';\n", "");
text = text.replace("import type { Config } from '../config/config.js';\n", "");
text = text.replace(
  "import { doesToolInvocationMatch } from './tool-utils.js';\n",
  "function doesToolInvocationMatch(..._args: unknown[]): boolean { return false; }\n",
);
writeFileSync(utils, text);

run(
  "npx",
  [
    "--yes",
    "esbuild@0.25.0",
    join(srcDir, "shellReadOnlyChecker.ts"),
    "--bundle",
    "--format=esm",
    "--platform=node",
    `--outfile=${join(here, "vendor", "qwen-readonly.mjs")}`,
  ],
  here,
);

writeFileSync(
  join(here, "vendor", "NOTICE"),
  `vendor/qwen-readonly.mjs is a tree-shaken bundle of Qwen Code's
isShellCommandReadOnly checker (Apache-2.0).

Source: https://github.com/QwenLM/qwen-code
Commit: ${sha}
Files: packages/core/src/utils/shellReadOnlyChecker.ts
       packages/core/src/utils/shell-safety-rules.ts
       packages/core/src/utils/shell-utils.ts

Rebuild: node install-qwen.mjs
`,
);

process.stderr.write(`landbridge-classifier: rebuilt qwen-readonly.mjs from ${sha}\n`);
