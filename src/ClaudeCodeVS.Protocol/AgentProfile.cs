namespace ClaudeCodeVs.Protocol;

/// <summary>
/// How an agent expects project-scoped MCP servers to be declared. The two shapes we know about are
/// structurally different enough that a filename knob isn't sufficient.
/// </summary>
public enum McpConfigFormat
{
    /// <summary>Claude Code's <c>.mcp.json</c>: <c>{"mcpServers":{"name":{"type":"stdio","command":…,"args":[…]}}}</c>.</summary>
    ClaudeMcpServers,

    /// <summary>opencode's <c>opencode.json</c>: <c>{"mcp":{"name":{"type":"local","command":[argv…],"enabled":true}}}</c>.</summary>
    OpenCodeMcp,
}

/// <summary>
/// The agent-specific launch/discovery surface (docs/MULTI-AGENT.md): where the lockfile lives, what
/// binary to run, which env vars tell it where the bridge is, and which of the three coupling systems
/// (IDE WebSocket, hooks, project MCP registration) it actually has.
///
/// The tool surface itself - diff, diagnostics, debugger, Roslyn navigation, tests, capture - is
/// agent-agnostic and lives behind this; a new agent is a new instance in <see cref="All"/>.
/// </summary>
public sealed class AgentProfile
{
    /// <summary>Stable key used for persistence and lookup. Never localized.</summary>
    public string Id { get; }

    /// <summary>Shown in logs, the panel's agent picker, and as the terminal tab / profile name.</summary>
    public string DisplayName { get; }

    /// <summary>The CLI binary to launch (resolved via PATH).</summary>
    public string Binary { get; }

    /// <summary>
    /// Absolute directory the agent scans for <c>&lt;port&gt;.lock</c> files, or null when the agent has
    /// no lockfile-based IDE discovery at all (see <see cref="SupportsIdeSocket"/>).
    /// </summary>
    public string? IdeDir { get; }

    /// <summary>The <c>ideName</c> value written into the lockfile, and the value our hooks filter on.</summary>
    public string IdeName { get; }

    /// <summary>HTTP header the agent presents the lockfile auth token in (WS upgrade + hook POSTs).</summary>
    public string AuthHeader { get; }

    /// <summary>Server name reported in the MCP `initialize` response.</summary>
    public string McpServerName { get; }

    /// <summary>Workspace subdirectory the agent reads config/scripts from (hook scripts, shim).</summary>
    public string ConfigDirName { get; }

    /// <summary>Settings file (under <see cref="ConfigDirName"/>) hook entries are merged into.</summary>
    public string SettingsFileName { get; }

    /// <summary>Workspace-root file MCP servers are registered in.</summary>
    public string McpConfigFileName { get; }

    /// <summary>Shape of the entries written into <see cref="McpConfigFileName"/>.</summary>
    public McpConfigFormat McpFormat { get; }

    /// <summary>False for agents with no PreToolUse/Stop/… hook system — skips hook install.</summary>
    public bool SupportsHooks { get; }

    /// <summary>False for agents with no project-scoped MCP registration — skips the MCP config install.</summary>
    public bool SupportsMcpRegistration { get; }

    /// <summary>
    /// True when the agent discovers and connects to the IDE WebSocket via a lockfile. False means the
    /// only channel it has is the pull MCP servers, so selection push, attachments, and the diff gate
    /// are all unavailable - the debugger/semantic/test tools still are.
    /// </summary>
    public bool SupportsIdeSocket => IdeDir != null;

    /// <summary>
    /// Panel copy explaining what this agent loses relative to Claude Code, or null when nothing is
    /// lost. Rendered as an informational banner when the agent is selected.
    /// </summary>
    public string? Limitations { get; }

    /// <summary>Env vars to set before launching the CLI so it auto-connects to the bridge.</summary>
    public IReadOnlyDictionary<string, string> EnvironmentFor(int port)
    {
        var env = new Dictionary<string, string>();
        foreach (var kv in _fixedEnv) env[kv.Key] = kv.Value;
        if (_portEnvVar != null) env[_portEnvVar] = port.ToString();
        return env;
    }

    private readonly string? _portEnvVar;
    private readonly IReadOnlyDictionary<string, string> _fixedEnv;

    public AgentProfile(string id, string displayName, string binary, string? ideDir, string? portEnvVar,
        IReadOnlyDictionary<string, string>? fixedEnv = null,
        string ideName = "Visual Studio",
        string authHeader = "x-claude-code-ide-authorization",
        string mcpServerName = "claude-code-vs",
        string configDirName = ".claude",
        string settingsFileName = "settings.json",
        string mcpConfigFileName = ".mcp.json",
        McpConfigFormat mcpFormat = McpConfigFormat.ClaudeMcpServers,
        bool supportsHooks = true,
        bool supportsMcpRegistration = true,
        string? limitations = null)
    {
        Id = id;
        DisplayName = displayName;
        Binary = binary;
        IdeDir = ideDir;
        _portEnvVar = portEnvVar;
        _fixedEnv = fixedEnv ?? new Dictionary<string, string>();
        IdeName = ideName;
        AuthHeader = authHeader;
        McpServerName = mcpServerName;
        ConfigDirName = configDirName;
        SettingsFileName = settingsFileName;
        McpConfigFileName = mcpConfigFileName;
        McpFormat = mcpFormat;
        SupportsHooks = supportsHooks;
        SupportsMcpRegistration = supportsMcpRegistration;
        Limitations = limitations;
    }

    /// <summary>The default agent: the `claude` CLI. Every channel is available.</summary>
    public static AgentProfile ClaudeCode { get; } = new(
        id: "claude-code",
        displayName: "Claude Code",
        binary: "claude",
        ideDir: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "ide"),
        portEnvVar: "CLAUDE_CODE_SSE_PORT",
        fixedEnv: new Dictionary<string, string> { ["ENABLE_IDE_INTEGRATION"] = "true" });

    /// <summary>
    /// opencode. It is already a client of the SAME IDE contract: it scans <c>~/.claude/ide/*.lock</c>,
    /// presents <c>x-claude-code-ide-authorization</c>, and speaks MCP <c>2025-11-25</c> - so it connects
    /// to our existing socket with no protocol work. It consumes only the <c>selection_changed</c> and
    /// <c>at_mentioned</c> notifications, and has no shell-command hook system (its equivalent is a JS
    /// plugin), so the diff gate is off and its MCP registration uses opencode.json's own shape.
    /// </summary>
    public static AgentProfile OpenCode { get; } = new(
        id: "opencode",
        displayName: "OpenCode",
        binary: "opencode",
        // Its lockfile scan is hardcoded to the legacy ~/.claude/ide path, same as ours - so one
        // lockfile serves both agents and no second file is written.
        ideDir: ClaudeCode.IdeDir,
        portEnvVar: "OPENCODE_EDITOR_SSE_PORT",
        configDirName: ".opencode",
        settingsFileName: "opencode.json",
        mcpConfigFileName: "opencode.json",
        mcpFormat: McpConfigFormat.OpenCodeMcp,
        supportsHooks: false,
        limitations: "OpenCode has no shell-command hook system, so the extension auto-deploys its "
            + "JS plugin (`.opencode/plugins/vs-diff-gate.js`) on Launch: the Accept/Reject diff gate and "
            + "turn-end notifications then work exactly like Claude Code's hooks. Break-state context "
            + "injection stays off (opencode has no per-turn context hook). Selection, attachments, and "
            + "the vs-debug / vs-semantic tools all work.");

    /// <summary>
    /// Oh My Pi (`omp`). No lockfile, no IDE WebSocket - its editor integration is stdio (ACP / RPC),
    /// which our in-proc bridge can't host. What it DOES have is an MCP client that imports a workspace
    /// <c>.mcp.json</c>, so the whole pull surface (debugger, semantic, tests, capture) ports as-is.
    /// </summary>
    public static AgentProfile OhMyPi { get; } = new(
        id: "oh-my-pi",
        displayName: "Oh My Pi",
        binary: "omp",
        ideDir: null,            // no lockfile discovery -> no IDE socket
        portEnvVar: null,
        configDirName: ".omp",
        settingsFileName: "config.yml",
        // omp imports a workspace .mcp.json alongside its native .omp/mcp.json, so the Claude shape works.
        mcpConfigFileName: ".mcp.json",
        supportsHooks: false,
        limitations: "Oh My Pi connects over stdio (ACP/RPC), not the IDE WebSocket, so the extension "
            + "auto-deploys a `.omp/extensions/vs-diff-gate.ts` stub on Launch: the diff gate and turn-end "
            + "toasts are restored; selection and attachments are reachable as pull tools "
            + "(vs_get_selection / vs_list_attachments) through its .mcp.json import. The IDE push "
            + "channels stay off - nothing is auto-injected into omp's context. The vs-debug / "
            + "vs-semantic MCP tools work via its .mcp.json import.");

    /// <summary>Every agent the extension can launch, in picker order. Claude Code is index 0 = default.</summary>
    public static IReadOnlyList<AgentProfile> All { get; } = new[] { ClaudeCode, OpenCode, OhMyPi };

    /// <summary>Look an agent up by <see cref="Id"/>, falling back to <see cref="ClaudeCode"/>.</summary>
    public static AgentProfile ById(string? id) =>
        All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)) ?? ClaudeCode;

    // ---------------- selection persistence ----------------
    //
    // Which agent the Launch button targets is a preference, not a safety gate, so unlike the panel's
    // auto-accept / drive / capture toggles it survives a restart. Stored as a bare id in LOCALAPPDATA
    // rather than a VS settings store so the Protocol project stays pure BCL (no VS SDK reference).

    private static string SelectionPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeCodeVS", "selected-agent.txt");

    /// <summary>The remembered agent, or <see cref="ClaudeCode"/> when nothing valid is stored.</summary>
    public static AgentProfile LoadSelected()
    {
        try
        {
            var path = SelectionPath;
            return File.Exists(path) ? ById(File.ReadAllText(path).Trim()) : ClaudeCode;
        }
        catch
        {
            return ClaudeCode; // an unreadable preference must never break startup
        }
    }

    /// <summary>Remember <paramref name="agent"/> as the Launch target. Best-effort.</summary>
    public static void SaveSelected(AgentProfile agent)
    {
        try
        {
            var path = SelectionPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, agent.Id);
        }
        catch (Exception e)
        {
            Log.Warn($"could not persist the selected agent: {e.Message}");
        }
    }
}
