/**
 * omp-vs-diff-gate.ts — diff gate + turn-end notifications for Oh My Pi (omp) behind Claude Code
 * for Visual Studio (docs/MULTI-AGENT.md). Auto-installed by the extension into your project's
 * extensions dir when you Launch Oh My Pi from the panel; drop it there manually too.
 *
 * omp has no IDE WebSocket and no shell-command hook system, so the extension's single-gate edit
 * review is OFF by default: edits apply directly through omp's own approval policy. This extension
 * restores the native VS diff gate through omp's in-process `tool_call` event: before a
 * file-modifying tool executes, we POST the proposed contents to the bridge's /permission endpoint
 * and return `{ block: true, reason }` when the user rejected the change in the VS diff — the
 * omp equivalent of Claude Code's PreToolUse hook.
 *
 * It also restores the turn-end notification: `turn_end` fires when the model finishes a turn and
 * omp is waiting on you — we POST the bridge's /notify endpoint, the same in-IDE toast Claude
 * Code users get from the Stop hook.
 *
 * And it restores the IDE push channels at prompt time: `before_agent_start` POSTs the bridge's
 * /agent-context endpoint (one round trip: {debug, selection, attachments}) and injects a hidden
 * context message carrying whatever is live — the debugger's break state (stop location, call
 * stack, locals — Claude Code's UserPromptSubmit hook), the user's current editor selection
 * (the selection_changed channel), and any attachments staged since the last turn (the
 * at_mentioned channel; omp reads them by path). Sections that are empty inject nothing.
 *
 * Install: drop this file into your agent (or project) extensions directory and restart omp
 *   ~/.omp/extensions/vs-diff-gate.js     (user-wide)
 *   <cwd>/.omp/extensions/vs-diff-gate.js / <cwd>/.pi/extensions/ (per-project)
 * (see https://github.com/can1357/oh-my-pi/blob/main/docs/skills/authoring-extensions.md)
 *
 * Works out of the box with the bridge running (the "Agent" panel, any agent). Uses only
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
async function findBridge(): Promise<{ port: number; token: string } | null> {
  const ideDir = join(homedir(), ".claude", "ide");
  if (!existsSync(ideDir)) return null;

  const cwd = process.cwd();
  const candidates = readdirSync(ideDir)
    .filter((f) => f.endsWith(".lock"))
    .map((f): { port: number; token: string; workspace?: string } | null => {
      try {
        const doc = JSON.parse(readFileSync(join(ideDir, f), "utf8")) as {
          ideName?: string;
          authToken: string;
          workspaceFolders?: string[];
        };
        if (doc.ideName !== "Visual Studio") return null; // another IDE's lockfile
        // The port is the FILENAME (<port>.lock) - the lockfile JSON has no port field.
        return { port: Number.parseInt(f, 10), token: doc.authToken, workspace: doc.workspaceFolders?.[0] };
      } catch {
        return null;
      }
    })
    .filter((c): c is { port: number; token: string; workspace?: string } =>
      !!c && !!c.token && Number.isFinite(c.port))
    // Prefer the workspace root that is the longest prefix of our cwd.
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
    const json = (await res.json()) as { allow?: unknown; reason?: unknown };
    return { allow: json.allow === true, reason: typeof json.reason === "string" ? json.reason : null };
  } catch {
    return { allow: true, reason: null }; // bridge unreachable -> fail-open, never block the agent
  }
}

interface AgentContext {
  debug?: {
    mode?: string;
    stoppedAt?: { file?: string; line?: number; function?: string };
    callStack?: { function?: string }[];
    args?: { name?: string; type?: string; value?: string }[];
    locals?: { name?: string; type?: string; value?: string }[];
  };
  selection?: {
    text?: string;
    filePath?: string | null;
    selection?: { start?: { line: number }; end?: { line: number } };
  };
  attachments?: { path?: string; fileName?: string; estTokens?: number | null; needsTool?: boolean }[];
}

/** Prompt-time IDE context: POST the bridge's /agent-context (one round trip for all three push
 *  channels). Returns the parsed JSON, or null when unreachable. */
async function agentContext(bridge: { port: number; token: string }): Promise<AgentContext | null> {
  try {
    const res = await fetch(`http://127.0.0.1:${bridge.port}/agent-context`, {
      method: "POST",
      headers: {
        "x-claude-code-ide-authorization": bridge.token,
        "content-type": "application/json",
      },
      body: JSON.stringify({ cwd: process.cwd() }),
    });
    return (await res.json()) as AgentContext;
  } catch {
    return null; // bridge unreachable -> inject nothing, never block the turn
  }
}

/** Render the break-state snapshot exactly like vs-debug-context-hook.ps1 does for Claude Code. */
function renderDebug(d: NonNullable<AgentContext["debug"]>): string {
  const lines = ["The Visual Studio debugger is paused at a breakpoint. Current runtime state:"];
  if (d.stoppedAt) {
    lines.push(`- Stopped at ${d.stoppedAt.file}:${d.stoppedAt.line} in ${d.stoppedAt.function}()`);
  }
  if (d.callStack?.length) {
    lines.push("- Call stack (innermost first):");
    for (const fr of d.callStack) lines.push(`    ${fr.function}`);
  }
  if (d.args?.length) {
    lines.push("- Arguments (current frame):");
    for (const a of d.args) lines.push(`    ${a.name} (${a.type}) = ${a.value}`);
  }
  if (d.locals?.length) {
    lines.push("- Locals (current frame):");
    for (const l of d.locals) lines.push(`    ${l.name} (${l.type}) = ${l.value}`);
  }
  return lines.join("\n");
}

/** Turn-end notification: POST {message} to the bridge's /notify endpoint (fire-and-forget). */
async function notify(message: string, bridge: { port: number; token: string }): Promise<void> {
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
    /* best-effort - a busy bridge must never break omp */
  }
}

export default async function vsDiffGate(pi: ExtensionAPI) {
  // Resolve once at load; the port/token live for the whole VS process session. The factory
  // async is fine - omp/pi awaits it before startup completes (docs: async factory functions).
  const bridge = await findBridge();

  // Presence heartbeat: omp never opens the IDE WebSocket, so the panel pill had no signal to turn
  // green. Beat /agent-heartbeat every 10s from extension load; the extension greens "Connected" on
  // the first beat, greys ~instantly on the session_shutdown bye below, and greys within ~40s if
  // omp dies without one (30s TTL + 10s sweep on the bridge side).
  if (bridge) {
    const post = (body: Record<string, unknown>) =>
      fetch(`http://127.0.0.1:${bridge.port}/agent-heartbeat`, {
        method: "POST",
        headers: {
          "x-claude-code-ide-authorization": bridge.token,
          "content-type": "application/json",
        },
        body: JSON.stringify(body),
      }).catch(() => {});
    const beat = () => post({ agent: "Oh My Pi" });
    beat();
    // unref: never keep omp alive just to beat (Node timers only; typed loosely for dom lib)
    (setInterval(beat, 10_000) as unknown as { unref?: () => void }).unref?.();
    // Explicit bye on session teardown -> the pill greys immediately instead of TTL-ing out.
    (pi.on as (ev: string, h: () => void | Promise<void>) => void)(
      "session_shutdown", () => void post({ agent: "Oh My Pi", bye: true }),
    );
  }

  // Throttle turn-end toasts: a "turn" ends on every model reply, but we only want the toast
  // when omp actually hands control back to the user (which may be several turns later).
  let lastToast = 0;

  pi.on("tool_call", async (event) => {
    // Only file-modifying tools can enter the diff; read-only tools pass straight through.
    if (event.toolName !== "write" && event.toolName !== "edit") return;

    const input = event.input as Record<string, unknown> | undefined;
    const filePath = typeof input?.filePath === "string" ? input.filePath : "";
    if (!input || !filePath || !bridge) return; // no bridge to review with -> let omp work normally

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

  // Prompt-time IDE context (the push channels, restored): before each agent turn, fetch
  // {debug, selection, attachments} in one round trip and inject whatever is live as a hidden
  // context message. before_agent_start handlers may return { message } (docs/extensions.md);
  // display:false keeps it out of the transcript UI while the model still sees it.
  (pi.on as (ev: string, h: (ev2: unknown, ctx?: unknown) => unknown) => void)(
    "before_agent_start", async () => {
      if (!bridge) return;
      const ide = await agentContext(bridge);
      if (!ide) return;

      const sections: string[] = [];
      if (ide.debug?.mode === "break") sections.push(renderDebug(ide.debug));

      const sel = ide.selection;
      if (sel?.text && sel.filePath) {
        // 1-based lines for humans/models; cap the text like every other bridge read.
        const start = (sel.selection?.start?.line ?? 0) + 1;
        const end = (sel.selection?.end?.line ?? 0) + 1;
        const text = sel.text.length > 2000 ? sel.text.slice(0, 2000) + "\n[truncated]" : sel.text;
        sections.push(`The user's current selection in Visual Studio (${sel.filePath}:${start}-${end}):\n${text}`);
      }

      if (ide.attachments?.length) {
        const list = ide.attachments
          .map((a) => `    ${a.path}${a.needsTool ? " (needs a tool/script to parse)" : ""}`)
          .join("\n");
        sections.push(`The user staged these files in the Visual Studio attachment tray - read them by path:\n${list}`);
      }

      if (!sections.length) return;
      return { message: { customType: "vs-ide-context", content: sections.join("\n\n"), display: false } };
    },
  );

  // Turn-end toast: POST the bridge's /notify endpoint when omp finishes a turn and waits for
  // the user. Throttled to one toast per 60s so long agent runs don't toast-spam.
  (pi.on as (ev: string, h: (args: unknown) => void | Promise<void>) => void)(
    "turn_end", async () => {
      if (!bridge) return;
      const now = Date.now();
      if (now - lastToast < 60_000) return;
      lastToast = now;
      await notify("Oh My Pi finished a turn and is waiting for you", bridge);
    },
  );
}