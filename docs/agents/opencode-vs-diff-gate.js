/**
 * opencode-vs-diff-gate.js — Accept/Reject diff gate + turn-end notifications for OpenCode behind
 * Claude Code for Visual Studio (docs/MULTI-AGENT.md). Auto-installed by the extension into
 * `.opencode/plugins/` when you Launch OpenCode from the panel; copy it there manually too.
 *
 * OpenCode has no shell-command hook system, so the extension's single-gate edit review
 * (PreToolUse -> POST /permission -> native VS diff) is OFF by default: edits apply directly.
 * This plugin restores it through opencode's in-process equivalent: the `tool.execute.before`
 * hook. Before a file-modifying tool runs, we stage the proposed new contents, POST them to the
 * bridge's /permission endpoint, and `throw` (opencode's veto mechanism) when the user rejected
 * the change in the VS diff — exactly what the PreToolUse hook does for Claude Code.
 *
 * It also restores the turn-end notification: the `event` hook watches session state and POSTs
 * the bridge's /notify endpoint when the session goes idle — the same in-IDE "finished / needs
 * input" toast Claude Code users get from the Stop hook.
 *
 * And it restores break-state context injection (Claude Code's UserPromptSubmit hook): the
 * `chat.message` hook fires on each user message, POSTs the bridge's /debug-context endpoint,
 * and when Visual Studio is paused at a breakpoint appends the live state — stop location, call
 * stack, locals/arguments — as a synthetic text part, so the model debugs from runtime values
 * without a tool call. No-op when not in break mode.
 *
 * Install: drop this file into your project's plugin directory as
 *   .opencode/plugins/vs-diff-gate.js
 * (opencode auto-loads every .js/.ts in .opencode/plugins/ — no config change needed).
 *
 * Works out of the box with the bridge running (the "Agent" panel, any agent — Launch OpenCode
 * from the panel picker). Requires Node's builtin `node:fs`/`node:net` only; no npm packages.
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
        if (doc.ideName !== "Visual Studio") return null; // another IDE's lockfile
        // The port is the FILENAME (<port>.lock) - the lockfile JSON has no port field.
        return { port: Number.parseInt(f, 10), token: doc.authToken, workspace: doc.workspaceFolders?.[0] };
      } catch {
        return null;
      }
    })
    .filter((c) => c && c.token && Number.isFinite(c.port))
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

/** Live debugger snapshot: POST the bridge's /debug-context (same endpoint Claude Code's
 *  UserPromptSubmit hook uses). Returns the parsed JSON, or null when unreachable. */
async function debugContext(bridge) {
  try {
    const res = await fetch(`http://127.0.0.1:${bridge.port}/debug-context`, {
      method: "POST",
      headers: {
        "x-claude-code-ide-authorization": bridge.token,
        "content-type": "application/json",
      },
      body: JSON.stringify({ cwd: process.cwd() }),
    });
    return await res.json();
  } catch {
    return null; // bridge unreachable -> inject nothing, never block the turn
  }
}

/** Render the break-state snapshot exactly like vs-debug-context-hook.ps1 does for Claude Code. */
function renderDebug(d) {
  const lines = ["The Visual Studio debugger is paused at a breakpoint. Current runtime state:"];
  if (d.stoppedAt) {
    lines.push(`- Stopped at ${d.stoppedAt.file}:${d.stoppedAt.line} in ${d.stoppedAt.function}()`);
  }
  if (Array.isArray(d.callStack) && d.callStack.length) {
    lines.push("- Call stack (innermost first):");
    for (const fr of d.callStack) lines.push(`    ${fr.function}`);
  }
  if (Array.isArray(d.args) && d.args.length) {
    lines.push("- Arguments (current frame):");
    for (const a of d.args) lines.push(`    ${a.name} (${a.type}) = ${a.value}`);
  }
  if (Array.isArray(d.locals) && d.locals.length) {
    lines.push("- Locals (current frame):");
    for (const l of d.locals) lines.push(`    ${l.name} (${l.type}) = ${l.value}`);
  }
  return lines.join("\n");
}

/** Turn-end notification: POST {message} to the bridge's /notify endpoint (fire-and-forget). */
async function notify(message, bridge) {
  try {
    await fetch(`http://127.0.0.1:${bridge.port}/notify`, {
      method: "POST",
      headers: {
        "x-claude-code-ide-authorization": bridge.token,
        "content-type": "application/json",
      },
      body: JSON.stringify({ message }),
    });
  } catch {
    /* best-effort - a busy bridge must never break opencode */
  }
}

/**
 * Plugin entry: returns the hook set. opencode calls this function per session with the
 * { project, client, $, directory, worktree } context.
 */
export const VsDiffGate = async () => {
  // Resolve once at plugin load; the port/token live for the whole VS process session.
  const bridge = await findBridge();

  // Presence heartbeat — deliberately NOT an interval. opencode can leave a background server
  // process alive after the TUI closes; an interval beat from that survivor would keep the panel
  // pill green forever. Instead: one beat at load (greens the pill before the WS attaches), then a
  // beat per real activity (hooks below, throttled). Steady-state presence is governed by the IDE
  // WebSocket, which opencode attaches within seconds - so closing it greys the pill instantly,
  // exactly like Claude Code, and an idle survivor process goes quiet.
  let lastBeat = 0;
  const beat = () => {
    if (!bridge) return;
    const now = Date.now();
    if (now - lastBeat < 5_000) return;
    lastBeat = now;
    fetch(`http://127.0.0.1:${bridge.port}/agent-heartbeat`, {
      method: "POST",
      headers: {
        "x-claude-code-ide-authorization": bridge.token,
        "content-type": "application/json",
      },
      body: JSON.stringify({ agent: "OpenCode" }),
    }).catch(() => {});
  };
  beat();

  return {
    "tool.execute.before": async (input, output) => {
      beat(); // activity presence (throttled)
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

    // Break-state context injection (Claude Code's UserPromptSubmit equivalent): when VS is paused
    // at a breakpoint as the user submits a message, append the live state as a synthetic text
    // part so the model sees runtime values alongside the prompt. Fail-open: any error or a
    // non-break mode injects nothing and the turn proceeds untouched.
    "chat.message": async (input, output) => {
      beat(); // activity presence (throttled)
      if (!bridge || !output?.parts) return;
      const d = await debugContext(bridge);
      if (!d || d.mode !== "break") return;
      output.parts.push({ type: "text", text: renderDebug(d), synthetic: true });
    },

    // Turn-end notification: when the session goes idle (model finished, waiting for input),
    // raise the same in-IDE toast the Stop hook gives Claude Code users. Filter to the events
    // opencode actually emits - session.* and message.part.updated - and only act on idle.
    event: async ({ event }) => {
      if (!bridge || !event) return;
      beat(); // activity presence (throttled)
      if (event.type === "session.idle") {
        await notify("OpenCode finished a turn and is waiting for you", bridge);
      }
    },
  };
};