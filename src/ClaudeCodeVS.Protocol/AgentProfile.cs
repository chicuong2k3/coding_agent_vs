namespace ClaudeCodeVs.Protocol;

/// <summary>
/// The agent-specific launch/discovery surface, per docs/MULTI-AGENT.md "Minimal First Step":
/// where the lockfile lives, what binary to run, and which env vars tell it where the bridge is.
/// Everything else (hooks, MCP registration, protocol extensions) stays Claude-hardcoded until a
/// second agent actually needs it.
/// </summary>
public sealed class AgentProfile
{
    /// <summary>Shown in logs and as the terminal tab / profile name.</summary>
    public string DisplayName { get; }

    /// <summary>The CLI binary to launch (resolved via PATH).</summary>
    public string Binary { get; }

    /// <summary>Absolute directory the agent scans for <c>&lt;port&gt;.lock</c> files.</summary>
    public string IdeDir { get; }

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

    /// <summary>False for agents with no PreToolUse/Stop/… hook system — skips hook install.</summary>
    public bool SupportsHooks { get; }

    /// <summary>False for agents with no project-scoped MCP registration — skips .mcp.json install.</summary>
    public bool SupportsMcpRegistration { get; }

    /// <summary>Env vars to set before launching the CLI so it auto-connects to the bridge.</summary>
    public IReadOnlyDictionary<string, string> EnvironmentFor(int port)
    {
        var env = new Dictionary<string, string>();
        foreach (var kv in _fixedEnv) env[kv.Key] = kv.Value;
        env[_portEnvVar] = port.ToString();
        return env;
    }

    private readonly string _portEnvVar;
    private readonly IReadOnlyDictionary<string, string> _fixedEnv;

    public AgentProfile(string displayName, string binary, string ideDir, string portEnvVar,
        IReadOnlyDictionary<string, string>? fixedEnv = null,
        string authHeader = "x-claude-code-ide-authorization",
        string mcpServerName = "claude-code-vs",
        string configDirName = ".claude",
        string settingsFileName = "settings.json",
        string mcpConfigFileName = ".mcp.json",
        bool supportsHooks = true,
        bool supportsMcpRegistration = true)
    {
        DisplayName = displayName;
        Binary = binary;
        IdeDir = ideDir;
        _portEnvVar = portEnvVar;
        _fixedEnv = fixedEnv ?? new Dictionary<string, string>();
        AuthHeader = authHeader;
        McpServerName = mcpServerName;
        ConfigDirName = configDirName;
        SettingsFileName = settingsFileName;
        McpConfigFileName = mcpConfigFileName;
        SupportsHooks = supportsHooks;
        SupportsMcpRegistration = supportsMcpRegistration;
    }

    /// <summary>The default (and currently only) agent: the `claude` CLI.</summary>
    public static AgentProfile ClaudeCode { get; } = new(
        displayName: "Claude Code",
        binary: "claude",
        ideDir: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "ide"),
        portEnvVar: "CLAUDE_CODE_SSE_PORT",
        fixedEnv: new Dictionary<string, string> { ["ENABLE_IDE_INTEGRATION"] = "true" });
}
