/**
 * opencode-vs-diff-gate.js — opt-in Accept/Reject diff gate for OpenCode behind Claude Code
 * for Visual Studio (docs/MULTI-AGENT.md).
 *
 * OpenCode has no shell-command hook system, so the extension's single-gate edit review
 * (PreToolUse -> POST /permission -> native VS diff) is OFF by default: edits apply directly.
 * This plugin restores it through opencode's in-process equivalent: the `tool.execute.before`
 * hook. Before a file-modifying tool runs, we stage the proposed new contents, POST them to the
 * bridge's /permission endpoint, and `throw` (opencode's veto mechanism) when the user rejected
 * the change in the VS diff — exactly what the PreToolUse hook does for Claude Code.
 *
 * Install: copy (or symlink) this file into your project's plugin directory as
 *   .opencode/plugins/vs-diff-gate.js
 * (opencode auto-loads every .js/.ts in .opencode/plugins/ — no config change needed).
 *
 * Works out of the box with the bridge running (the "Claude Code" panel, any agent — Launch
 * OpenCode from the panel picker). Requires Node's builtin `node:fs`/`node:net` only; no npm
 * packages.
 */

import { readdirSync, readFileSync, existsSync } from "node:fs";
import { join, dirname } from "node:path";
import { homedir } from "node:os";
import net from "node:net";

/**
 * Discover the live bridge exactly like the PowerShell hooks do (docs/MULTI-AGENT.md
 * "Lockfile Discovery"): scan ~/.claude/ide/*.lock, keep files whose ideName is the VS bridge,
 * prefer (longest prefix) matching the current working directory, probe each candidate's port,
 * and return the first that is actually listening.
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
function prefixLen(root, cwd) {
  if (!root || !cwd) return 0;
  const r = root.replace(/[\\/]+$/, "");
  return cwd.toLowerCase().startsWith(r.toLowerCase()) ? r.length : 0;
}

/** 300 ms TCP probe — dead lockfiles (recycled PIDs, crashed VS) must never gate us. */
function portOpen(port) {
  const { promise, resolve } = Promise.withResolvers();
  const s = net.connect({ host: "127.0.0.1", port, timeout: 300 });
  s.on("connect", () => { s.destroy(); resolve(true); });
  s.on("error", () => resolve(false));
  s.on("timeout", () => { s.destroy(); resolve(false); });
  return promise;
}

async function permission(filePath, newContents, bridge) {
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

/**
 * Plugin entry: returns the hook set. opencode calls this function per session with the
 * { project, client, $, directory, worktree } context.
 */
export const VsDiffGate = async () => {
  // Resolve once at plugin load; the port/token live for the whole VS process session.
  const bridge = await findBridge();

  return {
    "tool.execute.before": async (input, output) => {
      // Only file-modifying tools can enter the diff; read-only tools pass straight through.
      if (input.tool !== "write" && input.tool !== "edit") return;

      const filePath = output.args.filePath;
      if (!filePath || !bridge) return; // no bridge to review with -> let opencode work normally

      let newContents;
      if (input.tool === "write") {
        newContents = output.args.newContents ?? output.args.contents;
      } else {
        // edit carries {oldString, newString}; reconstruct the full proposed file so the VS
        // diff reads like the Claude Code one. If the file's on-disk state doesn't contain
        // oldString (stale call), POST what we can: opencode will fail its own edit anyway.
        let current = "";
        try { current = readFileSync(filePath, "utf8"); } catch { return; }
        newContents = current.replace(output.args.oldString ?? "", output.args.newString ?? "");
      }
      if (typeof newContents !== "string") return;

      const { allow, reason } = await permission(filePath, newContents, bridge);
      if (!allow) {
        // Throwing is opencode's documented veto: the tool call is aborted and the message
        // surfaces to the model, mirroring Claude Code's deny + optional feedback.
        throw new Error(reason || `Edit rejected in Visual Studio diff`);
      }
    },
  };
};