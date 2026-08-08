using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudeCodeVs.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Hooks;

/// <summary>
/// Registers the Phase 2 debug PULL channel as a project MCP server. Writes the embedded stdio shim into
/// the workspace's .claude/ folder and upserts an entry into the workspace .mcp.json so the CLI launches
/// it as an MCP server. The shim then discovers the live VS bridge and proxies JSON-RPC to POST /mcp,
/// where the real vs_* debug tools run (in-proc, EnvDTE-backed). Called from Launch alongside the hook
/// installer. Best-effort; idempotent; never throws into the launch path.
///
/// Note: a project-scoped .mcp.json makes the CLI prompt once to trust the server. That's expected.
/// </summary>
internal static class McpInstaller
{
    private const string ShimScript = "vs-mcp-shim.ps1";

    // The two pull-channel MCP servers, both backed by the same stdio shim (different -Route → different
    // bridge endpoint). vs-debug stays byte-identical to its original entry (no extra args → shim default
    // /mcp); vs-semantic adds -Route /mcp-semantic for the Roslyn code-navigation tools.
    private static readonly (string Name, string[] ExtraArgs)[] Servers =
    {
        ("vs-debug", new string[0]),
        ("vs-semantic", new[] { "-Route", "/mcp-semantic" }),
    };

    public static void EnsureInstalled(string workspaceRoot, AgentProfile? profile = null)
    {
        var agent = profile ?? AgentProfile.ClaudeCode;
        if (!agent.SupportsMcpRegistration)
        {
            Log.Info($"mcp: {agent.DisplayName} has no project MCP registration; skipping install");
            return;
        }
        try
        {
            // Where the shim lives. Agents that register into CLAUDE'S OWN .mcp.json (same file, same
            // ClaudeMcpServers shape - Oh My Pi imports a workspace .mcp.json wholesale) reuse Claude's
            // config dir, so the entry they write is byte-identical to Claude's: installing both agents
            // never flips the file back and forth. Agents with their own config file (opencode.json)
            // get their own dir.
            var shimDir = agent.McpConfigFileName == AgentProfile.ClaudeCode.McpConfigFileName
                          && agent.McpFormat == AgentProfile.ClaudeCode.McpFormat
                ? AgentProfile.ClaudeCode.ConfigDirName
                : agent.ConfigDirName;
            var claudeDir = Path.Combine(workspaceRoot, shimDir);
            Directory.CreateDirectory(claudeDir);

            // 1) (Over)write the shim from the embedded copy, so updates ship with the extension.
            File.WriteAllText(Path.Combine(claudeDir, ShimScript), ReadEmbeddedScript(ShimScript));

            // 2) Upsert the server entry into the agent's MCP config, preserving any other servers. The
            //    relative -File path resolves against the CLI's cwd (the workspace root), matching where
            //    the shim was written. We always (re)write OUR entry so command/args updates ship, but
            //    leave the rest of the file untouched.
            var mcpPath = Path.Combine(workspaceRoot, agent.McpConfigFileName);
            JObject root;
            if (File.Exists(mcpPath))
            {
                try { root = JObject.Parse(File.ReadAllText(mcpPath)); }
                catch (Exception e)
                {
                    Log.Warn($"mcp: couldn't parse {mcpPath}; leaving it alone ({e.Message})");
                    return;
                }
            }
            else
            {
                root = new JObject();
            }

            // Where the server map lives differs per agent: Claude's .mcp.json nests under "mcpServers",
            // opencode.json under "mcp". Only assign when creating: re-assigning an already-parented
            // JToken makes Json.NET CLONE it into the parent, detaching this local reference - upserts
            // would then never reach the file (same bug as the hook installer; matters for users
            // upgrading with an existing config).
            string mapKey = agent.McpFormat == McpConfigFormat.OpenCodeMcp ? "mcp" : "mcpServers";
            if (root[mapKey] is not JObject servers)
            {
                servers = new JObject();
                root[mapKey] = servers;
            }

            bool changed = false;
            foreach (var (name, extraArgs) in Servers)
            {
                var desired = BuildEntry(agent, extraArgs);
                if (JToken.DeepEquals(servers[name], desired)) continue;
                servers[name] = desired;
                changed = true;
            }

            if (!changed)
            {
                Log.Info($"mcp: 'vs-debug' + 'vs-semantic' already registered in {mcpPath}; nothing to change");
                return;
            }

            File.WriteAllText(mcpPath, root.ToString(Formatting.Indented));
            Log.Info($"mcp: registered 'vs-debug' + 'vs-semantic' MCP servers in {mcpPath} (pull channels)");
        }
        catch (Exception e)
        {
            Log.Warn($"mcp install failed: {e.Message}");
        }
    }

    /// <summary>
    /// The config entry for one pull server, in the shape <paramref name="agent"/> expects.
    ///
    /// Discovery arguments (-IdeDir / -IdeName / -AuthHeader) are appended ONLY when they differ from
    /// the shim's built-in Claude defaults, so Claude Code's entry stays byte-identical to the one
    /// shipped before multi-agent support - a changed entry re-triggers the CLI's "trust this project
    /// server?" prompt for every existing user.
    /// </summary>
    private static JObject BuildEntry(AgentProfile agent, string[] extraArgs)
    {
        // Same sharing rule as EnsureInstalled: agents that register into Claude's .mcp.json reuse
        // Claude's script path so the entries collide cleanly instead of flip-flopping.
        var shimDir = agent.McpConfigFileName == AgentProfile.ClaudeCode.McpConfigFileName
                      && agent.McpFormat == AgentProfile.ClaudeCode.McpFormat
            ? AgentProfile.ClaudeCode.ConfigDirName
            : agent.ConfigDirName;
        var script = $"{shimDir}/{ShimScript}";
        var tail = new List<string>(extraArgs);
        if (agent.IdeDir is { Length: > 0 } dir && !string.Equals(dir, AgentProfile.ClaudeCode.IdeDir, StringComparison.OrdinalIgnoreCase))
        {
            tail.Add("-IdeDir"); tail.Add(dir);
        }
        if (!string.Equals(agent.IdeName, AgentProfile.ClaudeCode.IdeName, StringComparison.Ordinal))
        {
            tail.Add("-IdeName"); tail.Add(agent.IdeName);
        }
        if (!string.Equals(agent.AuthHeader, AgentProfile.ClaudeCode.AuthHeader, StringComparison.OrdinalIgnoreCase))
        {
            tail.Add("-AuthHeader"); tail.Add(agent.AuthHeader);
        }

        if (agent.McpFormat == McpConfigFormat.OpenCodeMcp)
        {
            // opencode.json: `command` is the whole argv array, and entries carry an explicit enabled flag.
            var argv = new JArray("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script);
            foreach (var a in tail) argv.Add(a);
            return new JObject
            {
                ["type"] = "local",
                ["command"] = argv,
                ["enabled"] = true,
            };
        }

        var args = new JArray("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script);
        foreach (var a in tail) args.Add(a);
        return new JObject
        {
            ["type"] = "stdio",
            ["command"] = "powershell",
            ["args"] = args,
        };
    }

    private static string ReadEmbeddedScript(string scriptFileName)
    {
        var asm = typeof(McpInstaller).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(scriptFileName, StringComparison.OrdinalIgnoreCase));
        if (name == null)
            throw new InvalidOperationException($"embedded shim script not found: {scriptFileName}");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
