# Multi-Agent Architecture: Where to Generalize

Status: analysis for future work. This documents the current architecture and identifies every Claude Code coupling point, so supporting Oh My Pi, OpenCode, and other agents is a mechanical exercise rather than a re-architecture.

---

## Architecture Overview

The extension is a **protocol bridge** — zero model calls, zero agent logic. The `claude` CLI owns all agent work. The extension provides Visual Studio's half of the IDE-integration contract: a native diff window, diagnostics, selection context, live debugger, Roslyn navigation, test runner, and screen capture.

```
                          ┌────────────────────────────────────────────────┐
                          │               Visual Studio (net48 VSIX)        │
                          │                                                │
    CLI (claude)          │  ┌─────────────┐  ┌──────────────────────────┐ │
    ┌──────────┐          │  │  BridgeHost │  │     IdeWebSocketServer   │ │
    │ MCP/     │◄──WS─────│──┤  (wiring)   │──┤  HttpListener(:port)    │ │
    │ JSON-RPC │          │  └──────┬──────┘  │                          │ │
    │          │          │         │         │  / (WS → IDE MCP)        │─┼── 12 IDE tools
    │ PreTool  │──hook─── │─────────┼─────────│  /permission   (POST)    │ │   (openDiff, openFile,
    │ Use hook │          │         │         │  /usage        (POST)    │ │    getDiagnostics, …)
    │          │          │         │         │  /notify       (POST)    │ │
    │ UserPrompt│──hook── │─────────┼─────────│  /debug-context(POST)    │ │
    │ Submit   │          │         │         │  /mcp           (POST)   │─┼── vs-debug MCP (32 tools)
    │          │          │         │         │  /mcp-semantic  (POST)   │─┼── vs-semantic MCP (8 tools)
    │ MCP shim │──HTTP─── │─────────┘         └──────────────────────────┘ │
    │ (stdio→  │          │                                                │
    │  HTTP)   │          │  ┌──────────┐  ┌──────────┐  ┌─────────────┐  │
    └──────────┘          │  │  Hooks/  │  │  Tools/  │  │  CodeModel/ │  │
                          │  │installer │  │ (IIdeTool│  │ RoslynReader │  │
                          │  │          │  │  impls)  │  │             │  │
                          │  └──────────┘  └──────────┘  └─────────────┘  │
                          │                                                │
                          └────────────────────────────────────────────────┘
```

### The Three Surfaces

| Surface | Transport | Auth | What it carries |
|---|---|---|---|
| **IDE WebSocket** | WS upgrade on `/` | `x-claude-code-ide-authorization` header at upgrade | IDE protocol: `openDiff`, `openFile`, `getDiagnostics`, `selection_changed`, … |
| **Hook endpoints** | HTTP POST | Same auth header from lockfile | `/permission` (single-gate diff), `/usage` (token stats), `/notify` (user attention), `/debug-context` (live break state) |
| **Pull MCP servers** | HTTP POST, one JSON-RPC per request | Same auth header | `/mcp` (32 debug/test/capture tools), `/mcp-semantic` (8 Roslyn navigation tools) |

## Data Flow Per Operation

### 1. IDE Connection (WebSocket)

```
CLI reads ~/.claude/ide/<port>.lock
  → finds pid, workspaceFolders, ideName="Visual Studio", authToken
  → opens WS to ws://127.0.0.1:<port>/ with auth header
  → server validates token, echoes Sec-WebSocket-Protocol: mcp
  → MCP initialize/initialized handshake
  → CLI sends ide_connected notification {pid}
  → CLI calls closeAllDiffTabs proactively
  → bidirectional MCP/JSON-RPC 2.0 for the session
```

### 2. Edit Review (Single-Gate Diff)

```
CLI proposes edit (Write/Edit/MultiEdit tool)
  → PreToolUse hook fires in PowerShell
  → hook reads stdin JSON, reconstructs file content
  → hook discovers live bridge via lockfile scan
  → hook POSTs {filePath, newContents} to /permission with auth token
  → BridgeHost.PermissionHandler shows native VS diff (InfoBar)
  → diff parks on TaskCompletionSource (deferred — convention #3)
  → user clicks Accept/Reject/Reject+feedback
  → TCS resolves → /permission returns {allow, reason?}
  → hook returns decision to CLI (allow/deny)
  → CLI writes file on allow; reverts on deny
```

### 3. Debugger Push (Context Injection)

```
User paused at breakpoint
  → UserPromptSubmit hook fires
  → hook POSTs to /debug-context
  → server reads EnvDTE DebuggerReader (UI thread, 2s cap)
  → returns break state JSON: {mode, stoppedAt, callStack, args, locals}
  → hook renders human-readable context block
  → injected as additionalContext for this turn
```

### 4. Debugger Pull (On-Demand MCP)

```
CLI loads vs-debug MCP server (registered via .mcp.json)
  → launches vs-mcp-shim.ps1 as stdio MCP server
  → shim discovers live bridge (same lockfile logic)
  → for each JSON-RPC message on stdin:
    → forwards to POST http://127.0.0.1:<port>/mcp?Route=/mcp
    → returns response JSON on stdout
  → tool logic runs in-proc against EnvDTE on UI thread
```

### 5. Semantic Navigation (Roslyn Pull)

```
Same shim, different route: -Route /mcp-semantic
  → shim POSTs to /mcp-semantic
  → RoslynReader resolves symbolId/position via VisualStudioWorkspace
  → runs SymbolFinder off UI thread (Roslyn is free-threaded)
  → returns typed results (references, hierarchy, decompiled source)
```

## Lockfile Discovery (Shared by All Hooks + Shim)

Every PowerShell script uses identical logic:
1. Scan `~/.claude/ide/*.lock`
2. Filter to `ideName == "Visual Studio"`
3. Score by workspace match: `cwd.StartsWith(workspaceFolders[0])` → bonus; longer prefix = higher score
4. Sort descending, pick first whose port is **actually listening** (300ms TCP connect test)
5. Read `authToken` from that lockfile

This defeats: zombie lockfiles (dead process, port doesn't answer), recycled PIDs (port probe catches it), and parent-folder shadowing (longest prefix wins).

## Coupling Matrix: What's Claude-Specific

| Component | File(s) | Coupling | Generalization strategy |
|---|---|---|---|
| **Lockfile path** | `Lockfile.cs` | `~/.claude/ide/<port>.lock` | ✅ Done — `AgentProfile.IdeDir`, passed to `CreateForFreePort`/`ReapStale` |
| **Lockfile schema** | `Lockfile.cs` | `ideName`, `transport: "ws"`, `authToken`, `workspaceFolders`, `runningInWindows` | ✅ Kept — this IS the shared multi-agent contract now: opencode reads the same file/fields, and the bridge writes one lockfile per DISTINCT discovery dir (see "The lockfile is now a SHARED multi-agent contract" above) |
| **WS subprotocol** | `IdeWebSocketServer.cs` | MUST echo `Sec-WebSocket-Protocol: mcp` | ✅ Already dynamic — echoes whatever subprotocol the client offers |
| **Auth header** | `IdeWebSocketServer.cs` | `x-claude-code-ide-authorization` | ✅ Done — server accepts ANY registered `AgentProfile.AuthHeader`; one token gates all |
| **MCP protocol version** | `McpServer.cs:17` | `2025-11-25` (echoed from client) | Already dynamic — echoes client's version |
| **Server name** | `McpServer.cs` | `"claude-code-vs"` | ✅ Done — `AgentProfile.McpServerName` → `McpServer` ctor |
| **Env vars** | `AgentProfile.cs` | `ENABLE_IDE_INTEGRATION`, `CLAUDE_CODE_SSE_PORT` | ✅ Done — `AgentProfile.EnvironmentFor(port)` |
| **CLI binary** | `AgentProfile.cs` | `claude` | ✅ Done — `AgentProfile.Binary` |
| **Launch command** | `BridgeHost.cs` `LaunchAgentAsync` | `cmd.exe /K <binary>` with env vars | ✅ Done — both native-terminal and external paths take the profile |
| **Hook event names** | `PermissionHookInstaller.cs` | `PreToolUse`, `Stop`, `UserPromptSubmit`, `Notification` | ⚙️ Gated — `AgentProfile.SupportsHooks=false` skips install for agents without hooks; event names/contract stay Claude's until a second agent's hook system is known |
| **Hook contract** | `*.ps1` scripts | stdin JSON, stdout JSON, exit codes, specific field names (`permissionDecision`, `additionalContext`) | ⚙️ Gated behind `SupportsHooks` — per-agent script adapters when needed |
| **Hook settings path** | `PermissionHookInstaller.cs` | `.claude/settings.json` | ✅ Done — `AgentProfile.ConfigDirName` + `SettingsFileName` |
| **MCP registration** | `McpInstaller.cs` | `.mcp.json` with `type: "stdio"`, `command: "powershell"` | ✅ Path done (`McpConfigFileName`); format stays Claude's, `SupportsMcpRegistration=false` skips |
| **Hook install dir** | `McpInstaller.cs` | `.claude/` workspace subdirectory | ✅ Done — `AgentProfile.ConfigDirName`; agents registering into Claude's own `.mcp.json` (Oh My Pi) reuse Claude's dir so entries stay byte-identical |
| **`ide_connected` notif** | Protocol | CLI sends after handshake | ✅ Already tolerant — receive-side; unknown notifications are ignored, absence is harmless |
| **`at_mentioned` notif** | Protocol | `{filePath, lineStart?, lineEnd?}` → chip in composer | ⚙️ Send-side; an agent that ignores unknown notifications needs no change — gate only if one chokes |
| **`closeAllDiffTabs`** | Protocol | CLI calls proactively on connect | ✅ Already tolerant — a tool the agent may or may not call |

## What's Agent-Agnostic (The Core Value)

| Component | Backend | Why it's portable |
|---|---|---|
| **`openDiff`** | VS `IVsDifferenceService` | Any agent that proposes file changes can feed a diff |
| **`openFile`** | VS `IVsWindowFrame` / RDT | Any agent can ask to open a file |
| **`getDiagnostics`** | VS Error List (`IVsTaskList`) | Any agent can consume compiler errors |
| **`getCurrentSelection`** | VS Editor `SelectionService` | Any agent can read what the user is looking at |
| **`getOpenEditors` / `saveDocument`** | VS RDT | Any agent can manage editor state |
| **vs-debug tools** | EnvDTE + ClrMD + TestWindow | Any agent can read/drive the debugger |
| **vs-semantic tools** | Roslyn `VisualStudioWorkspace` | Any agent can navigate C# code semantically |
| **Screen capture** | Win32/GDI `PrintWindow` | Any agent can ask for screenshots |
| **Attachments** | File staging + `at_mentioned`-style push | Any agent with a file-reference mechanism |
| **Test runner** | VS Test Explorer engine | Any agent can run/debug/hunt tests |
| **Data breakpoints** | Concord debug-engine component | Any agent can set watchpoints |

## Suggested Refactoring Strategy

The agent-specific layer can be modeled as a set of **provider interfaces**:

```
IAgentProtocol
├── ILockfileProvider      → path, schema, discovery
├── ITransportConfig       → subprotocol, auth header name, env vars
├── ILaunchProfile         → binary, args, env, working dir, terminal
├── IHookProvider          → event names, script templates, settings path
├── IMcpRegistration       → config format, shim script, server entries
└── IProtocolExtensions    → agent-specific notifications (ide_connected, at_mentioned, …)
```

Current state: everything is hardcoded for Claude Code. The refactoring would:
1. Extract each agent-specific piece behind an interface
2. Implement `ClaudeCodeProtocol` as one concrete implementation (current behavior)
3. Add `OhMyPiProtocol`, `OpenCodeProtocol`, etc. as new implementations
4. Let `BridgeHost` select a protocol based on a registry or startup parameter

The `IIdeTool` / `McpServer` / tool implementations remain untouched — they're the agent-agnostic core.

### Status — profiles shipped AND wired

`AgentProfile` (`src/ClaudeCodeVS.Protocol/AgentProfile.cs`) is the flat one-class realization of the interface sketch above — every knob a second agent needs, with Claude Code as the default instance:

- **Discovery/launch:** `IdeDir`, `Binary`, `EnvironmentFor(port)`, `DisplayName` — wired through `Lockfile`, `BridgeHost.LaunchAgentAsync`, `VsTerminalLauncher`
- **Transport:** `AuthHeader` (→ `IdeWebSocketServer` ctor), `McpServerName` (→ `McpServer` ctor); WS subprotocol already echoes the client's offer
- **Config surface:** `ConfigDirName`, `SettingsFileName`, `McpConfigFileName` (+ `McpFormat`) — (→ both installers)
- **Capability gates:** `SupportsHooks`, `SupportsMcpRegistration` — agents without those systems skip the installs cleanly
- **Selection:** `All`, `ById`, `LoadSelected`/`SaveSelected` — the panel's agent picker persists the Launch target in `%LOCALAPPDATA%` (a preference, not a safety gate)

**The bridge serves every agent at once** (one port, one auth token, a lockfile in each distinct discovery dir — Claude Code and opencode share `~/.claude/ide`, so one file; the WS server accepts any registered auth header). The picker only decides which CLI `Launch` spawns. Three agents are shipped:

| Agent | Profile | Channels | Diff gate |
|---|---|---|---|
| Claude Code | `AgentProfile.ClaudeCode` | WS IDE + hooks + .mcp.json | ✅ PreToolUse hook → native VS diff |
| OpenCode | `AgentProfile.OpenCode` | WS IDE (same lockfile/header/MCP 2025-11-25) + opencode.json MCP | ✅ Auto-deployed plugin — `.opencode/plugins/vs-diff-gate.js` written on Launch (diff gate + turn-end toasts) |
| Oh My Pi (`omp`) | `AgentProfile.OhMyPi` | .mcp.json MCP import only (stdio, no WS) | ✅ Auto-deployed extension — `.omp/extensions/vs-diff-gate.ts` written on Launch (diff gate + turn-end toasts; selection/attachments as pull tools `vs_get_selection` / `vs_list_attachments`) |

Deliberately NOT abstracted (wait for a real second agent's hook/transport contract): the hook event names + ps1 script contract and the `.mcp.json` entry format — each is either the shared IDE-protocol contract itself or gated off by the capability flags.

### The lockfile is now a SHARED multi-agent contract

The lockfile JSON schema (`pid`, `pidStartTime`, `workspaceFolders`, `ideName`, `transport`, `runningInWindows`, `authToken`) is no longer Claude-only internals: **opencode is a consumer of the same file** — it scans the same `~/.claude/ide/*.lock` directory, filters the same `ideName`, and presents the same `authToken` header. Treat schema changes as breaking for every agent, and document them here.

---

## How to Turn On the Extension in VS 2026

### Install

- **Marketplace:** Extensions > Manage Extensions, search "Claude Code for Visual Studio"
- **Sideload:** Download `.vsix` from [Releases](https://github.com/firish/claude_code_vs/releases), double-click

### Requirements

- Visual Studio 2026
- Claude Code CLI installed and authenticated (`claude --version` → 2.1.191 tested)

### Launch

1. Open a solution/project in VS 2026 (projects needed for diagnostics and semantic tools)
2. **View > Other Windows > Claude Code** (also on Tools menu)
3. Pick the agent from the panel's **Agent** dropdown (persisted across restarts), then click **Launch** — the CLI opens in VS's docked Terminal window, auto-connected
4. The panel pill turns green: **Connected**. No `/ide` needed.
5. Ask the agent to make a change — Claude Code edits open as native VS diffs; OpenCode/omp edits go through the auto-deployed `docs/agents/` stub plugins (same VS diff), each auto-written into the agent's plugin dir on Launch

### Panel Controls

| Control | Default | What it does |
|---|---|---|
| **Agent** dropdown | Claude Code | Which CLI the Launch button spawns. The bridge serves every agent at once; this only picks the launch target (persists across restarts) |
| **Auto-accept (run wild)** | OFF | Applies edits without opening the diff |
| **Allow agent to drive debugger** | OFF | Continue/step/breakpoints/attach/break-on-thrown |
| **Allow screen capture** | OFF | `vs_capture_window` / `vs_capture_screen` |
| **Notify** | ON | In-IDE "turn finished" / "needs input" notifications |

All safety toggles reset each session; the agent selection is a preference and does not.

### Build from Source

```powershell
msbuild src/ClaudeCodeVS/ClaudeCodeVS.csproj /t:Rebuild /p:Configuration=Release
# Debug: F5 in VS to launch experimental instance
```
