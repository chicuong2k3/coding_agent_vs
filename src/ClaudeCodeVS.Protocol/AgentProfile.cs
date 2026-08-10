namespace ClaudeCodeVs.Protocol;

/// <summary>
/// How an agent expects project-scoped MCP servers to be declared. The two shapes we know about are
/// structurally different enough that a filename knob isn't sufficient.
/// </summary>
public enum McpConfigFormat
{
    /// <summary>Claude Code's <c>.mcp.json</c>: <c>{"mcpServers":{"name":{"type":"stdio","command":…,"args":[…]}}}</c>. Also the shape Oh My Pi imports via a workspace <c>.mcp.json</c>.</summary>
    ClaudeMcpServers,
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
    /// True for agents whose TUI can't render in VS's native Terminal tool window (full-screen
    /// alternate-buffer TUIs come up as a blank screen there; Claude Code's inline TUI is fine).
    /// Launch then goes straight to the external cmd.exe console, same as the "External console"
    /// button.
    /// </summary>
    public bool PreferExternalConsole { get; }

    /// <summary>
    /// True when the agent discovers and connects to the IDE WebSocket via a lockfile. False means the
    /// live WS notifications (selection_changed / at_mentioned) don't flow - instead the auto-deployed
    /// agent stub restores everything at prompt time: the diff gate, turn-end toasts, and a
    /// before-each-turn /agent-context injection of break state + current selection + newly staged
    /// attachments. Selection/attachments also stay reachable as pull tools (vs_get_selection /
    /// vs_list_attachments).
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
        bool preferExternalConsole = false,
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
        PreferExternalConsole = preferExternalConsole;
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
    /// Oh My Pi (`omp`). No lockfile, no IDE WebSocket - its editor integration is stdio (ACP / RPC),
    /// which our in-proc bridge can't host. What it DOES have is an MCP client that imports a workspace
    /// <c>.mcp.json</c>, so the whole pull surface (debugger, semantic, tests, capture) ports as-is,
    /// and the auto-deployed <c>.omp/extensions/vs-diff-gate.ts</c> stub restores the diff gate,
    /// turn-end toasts, AND the push channels: its <c>before_agent_start</c> hook injects break state,
    /// the current selection, and newly staged attachments via one <c>/agent-context</c> round trip.
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
        // Full-screen TUI: blank in VS's native terminal -> external console. No limitations banner:
        // the auto-deployed stub restores full parity (diff gate, toasts, prompt-time push of break
        // state/selection/attachments).
        preferExternalConsole: true);

    /// <summary>Every agent the extension can launch, in picker order. Claude Code is index 0 = default.</summary>
    public static IReadOnlyList<AgentProfile> All { get; } = new[] { ClaudeCode, OhMyPi };

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
