/**
 * omp-vs-diff-gate.ts — opt-in Accept/Reject diff gate for Oh My Pi (omp) behind Claude Code
 * for Visual Studio (docs/MULTI-AGENT.md).
 *
 * omp has no IDE WebSocket and no shell-command hook system, so the extension's single-gate edit
 * review is OFF by default: edits apply directly through omp's own approval policy. This extension
 * restores the native VS diff gate through omp's in-process `tool_call` event: before a
 * file-modifying tool executes, we POST the proposed contents to the bridge's /permission endpoint
 * and return `{ block: true, reason }` when the user rejected the change in the VS diff — the
 * omp equivalent of Claude Code's PreToolUse hook.
 *
 * Install: drop this file into your agent (or project) extensions directory and restart omp
 *   ~/.omp/agent/extensions/vs-diff-gate.ts     (user-wide)
 *   <cwd>/.omp/extensions/vs-diff-gate.ts      (per-project)
 * (see https://github.com/can1357/oh-my-pi/blob/main/docs/skills/authoring-extensions.md)
 *
 * Works out of the box with the bridge running (the "Claude Code" panel, any agent). Uses only
 * Node builtins — no npm packages.
 *
 * Note omp's own permission system stays in front of this; the VS diff is an ADDITIONAL review
 * layer for project-code edits, mirroring what Claude Code users get.
 */

import { readdirSync, readFileSync, existsSync } from "node:fs";
import { join } from "node:path";
import { homedir } from "node:os";
import net from "node:net";
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";

/**
 * Discover the live bridge exactly like the PowerShell hooks do (docs/MULTI-AGENT.md
 * "Lockfile Discovery"): scan ~/.claude/ide/*.lock, keep files whose ideName is the VS bridge,
 * prefer (longest prefix) matching the current working directory, probe each candidate's port,
 * and return the first that is actually listening. omp reads the SAME shared lockfile contract.
 */
async function findBridge() {
  const ideDir = join(homedir(), ".claude", "ide");
  if (!existsSync(ideDir)) return null;

  const cwd = process.cwd();
  const candidates = readdirSync(ideDir)
    .filter((f) => f.endsWith(".lock"))
    .map((f) => {
      try {
        const doc = JSON.parse(readFileSync(join(ideDir, f), "utf8"));
        return { port: doc.port, token: doc.authToken, workspace: doc.workspaceFolders?.[0] };
      } catch {
        return null;
      }
    })
    .filter((c) => c && c.token)
    // Prefer the lockfile whose workspace folder is the longest prefix of our cwd.
    .sort(
      (a, b) =>
        prefixLen(b.workspace, cwd) - prefixLen(a.workspace, cwd) ||
        (b.workspace ?? "").length - (a.workspace ?? "").length,
    );

  for (const c of candidates) {
    if (await portOpen(c.port)) return c; // first LIVING bridge, best workspace match first
  }
  return null;
}

function prefixLen(root: string | undefined, cwd: string): number {
  if (!root || !cwd) return 0;
  const r = root.replace(/[\\/]+$/, "");
  return cwd.toLowerCase().startsWith(r.toLowerCase()) ? r.length : 0;
}

/** 300 ms TCP probe — dead lockfiles (recycled PIDs, crashed VS) must never gate us. */
function portOpen(port: number): Promise<boolean> {
  const { promise, resolve } = Promise.withResolvers<boolean>();
  const s = net.connect({ host: "127.0.0.1", port, timeout: 300 });
  s.on("connect", () => { s.destroy(); resolve(true); });
  s.on("error", () => resolve(false));
  s.on("timeout", () => { s.destroy(); resolve(false); });
  return promise;
}

async function permission(filePath: string, newContents: string, bridge: { port: number; token: string }): Promise<{ allow: boolean; reason: string | null }> {
  try {
    const res = await fetch(`http://127.0.0.1:${bridge.port}/permission`, {
      method: "POST",
      headers: {
        "x-claude-code-ide-authorization": bridge.token,
        "content-type": "application/json",
      },
      body: JSON.stringify({ filePath, newContents }),
    });
    const json = await res.json();
    return { allow: json.allow === true, reason: json.reason };
  } catch {
    return { allow: true, reason: null }; // bridge unreachable -> fail-open, never block the agent
  }
}

export default function vsDiffGate(pi: ExtensionAPI) {
  // Resolve once at load; the port/token live for the whole VS process session.
  const bridge = findBridge();

  pi.on("tool_call", async (event) => {
    // Only file-modifying tools can enter the diff; read-only tools pass straight through.
    if (event.toolName !== "write" && event.toolName !== "edit") return;

    const input = event.input as Record<string, unknown> | undefined;
    const filePath = typeof input?.filePath === "string" ? input.filePath : "";
    if (!filePath || !bridge) return; // no bridge to review with -> let omp work normally

    let newContents: string | undefined;
    if (event.toolName === "write") {
      // omp's write carries the full new file content.
      newContents = typeof input.contents === "string" ? input.contents
        : typeof input.newContents === "string" ? input.newContents
        : undefined;
    } else if (typeof input?.oldString === "string" && typeof input?.newString === "string") {
      // edit carries {oldString, newString}; reconstruct the full proposed file so the VS diff
      // reads like the Claude Code one. Stale oldString (file moved on) -> omp's own edit fails
      // anyway, so gate on whatever the replacement would produce.
      try {
        newContents = readFileSync(filePath, "utf8").replace(input.oldString, input.newString);
      } catch {
        return;
      }
    }
    if (typeof newContents !== "string") return;

    const { allow, reason } = await permission(filePath, newContents, bridge);
    if (!allow) {
      // `tool_call` handlers returning { block: true } abort the call and surface the reason to
      // the model — omp's documented veto, mirroring Claude Code's deny + optional feedback.
      return { block: true, reason: reason ?? "Edit rejected in Visual Studio diff" };
    }
  });
}