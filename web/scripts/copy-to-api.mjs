import { cpSync, rmSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const target = join(root, "..", "backend", "Api", "wwwroot");
rmSync(target, { recursive: true, force: true });
cpSync(join(root, "out"), target, { recursive: true });
console.log(`Copied static site to ${target}`);
