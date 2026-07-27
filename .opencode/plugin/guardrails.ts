// .opencode/plugin/guardrails.ts
//
// Regel-Durchsetzung für C# / Dalamud-Projekte.
// Blockt gefährliche Bash-Commands, Secrets im Diff und Writes in geschützte Pfade.
// Kein Design-System-Check (ImGui, kein Web-UI).
// Kein Kennzeichen-Regex (produziert in C#-Code massenhaft False Positives).

import type { Plugin } from "@opencode-ai/plugin";
import { appendFileSync, mkdirSync } from "node:fs";
import { join, dirname, basename } from "node:path";

const LOG = join(process.cwd(), ".opencode", "guardrails.log");
try { mkdirSync(dirname(LOG), { recursive: true }); } catch {}
function log(msg: string) {
  try { appendFileSync(LOG, `[${new Date().toISOString()}] ${msg}\n`); } catch {}
}

// ---------- Regeln ----------

const DANGEROUS_BASH = [
  /\brm\s+-rf\s+(\/|~|\$HOME)/,
  /\bgit\s+push\s+.*\s(-f|--force)/,
  /\bgit\s+reset\s+--hard\s+origin/,
  /\bgit\s+push\s+.*\smain\b/,
  /\bnpm\s+publish/,
  /\bDROP\s+TABLE/i,
  /\bTRUNCATE\s+TABLE/i,
];

const SECRET_PATTERNS: Array<[RegExp, string]> = [
  [/\b(sk|pk)-[A-Za-z0-9]{20,}/,                       "API-Key (sk-/pk-)"],
  [/\bghp_[A-Za-z0-9]{30,}/,                           "GitHub PAT"],
  [/-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----/,  "Private Key"],
  [/\bAKIA[0-9A-Z]{16}\b/,                             "AWS Access Key"],
  [/postgres:\/\/[^:]+:[^@]+@/,                        "Postgres-URL mit Passwort"],
  // Kennzeichen-Muster bewusst weggelassen – False Positives in C#
];

const PROTECTED_PATHS = [
  /(^|[\\/])\.env(\.[^\\/]+)?(\b|$)/,
  /[\\/]secrets[\\/]/,
  /[\\/]keys[\\/]/
  /[\\/]dumps?[\\/]/
  /[\\/]backups?[\\/]/
  /[\\/]\.opencode[\\/]plugin[\\/]/
];

function block(reason: string): never {
  log(`BLOCK ${reason}`);
  throw new Error(`[Guardrail] ${reason}`);
}

// ---------- Plugin ----------

export const GuardrailsPlugin: Plugin = async ({ $ }) => {
  log("PLUGIN_INIT");

  return {
    "tool.execute.before": async (input, output) => {
      const tool = String((input as any)?.tool ?? "").toLowerCase();
      const args = (output as any)?.args as Record<string, unknown> | undefined;
      if (!args || typeof args !== "object") return;

      log(`HOOK tool=${tool} argKeys=${Object.keys(args).join(",")}`);

      // --- Bash / Shell ---
      if (tool === "bash" || tool === "shell") {
        const cmd = String(args.command ?? "");

        for (const pat of DANGEROUS_BASH) {
          if (pat.test(cmd)) {
            block(`Gefährliches Bash-Muster: ${pat}. Falls wirklich nötig: manuell im Terminal.`);
          }
        }
        for (const pat of PROTECTED_PATHS) {
          if (pat.test(cmd)) {
            block(`Bash-Command referenziert geschützten Pfad. Manuell ausführen, wenn wirklich nötig.`);
          }
        }
        for (const [pat, name] of SECRET_PATTERNS) {
          const hit = cmd.match(pat);
          if (hit) {
            block(`${name} in Bash-Command: "${hit[0].slice(0, 30)}...". Nichts committen.`);
          }
        }
        return;
      }

      // --- Edit / Write ---
      if (tool === "edit" || tool === "write") {
        const path = String(args.filePath ?? args.path ?? args.file_path ?? "");
        const content = String(
          args.content ?? args.newString ?? args.new_string ?? args.text ?? ""
        );

        for (const pat of PROTECTED_PATHS) {
          if (pat.test(path)) {
            block(`${basename(path)} ist geschützt.`);
          }
        }

        for (const [pat, name] of SECRET_PATTERNS) {
          const hit = content.match(pat);
          if (hit) {
            block(`${name} im Diff: "${hit[0].slice(0, 30)}...". Nichts committen.`);
          }
        }
      }
    },
  };
};

export default GuardrailsPlugin;