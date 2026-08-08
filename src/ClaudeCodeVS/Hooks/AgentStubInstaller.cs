using System;
using System.IO;
using System.Linq;
using ClaudeCodeVs.Protocol;

namespace ClaudeCodeVs.Hooks;

/// <summary>
/// Auto-deploys the per-agent diff-gate/no:stub plugins into the workspace on Launch, so
/// opencode and Oh My Pi get the same Accept/Reject diff gate + turn-end toasts as Claude Code
/// without any manual copying (the docs/agents/ stubs are the source of truth; this shoves the
/// embedded copies into each agent's auto-loaded plugin/extensions dir).
///
/// - opencode:  &lt;workspace&gt;\.opencode\plugins\   (every .js/.ts there is loaded at startup)
/// - oh-my-pi:  &lt;workspace&gt;\.omp\extensions\     (auto-discovered project-local extensions)
///
/// Only runs for agents that ship a stub (no shell hooks of their own): Claude Code is never
/// touched. Idempotent and best-effort - never throws into the launch path. The write is
/// content-checked, so repeated launches don't churn the file (which would re-trigger
/// opencode's plugin reload).
/// </summary>
internal static class AgentStubInstaller
{
    // Agent id -> (embedded resource suffix, relative write path). omp's project-local dir is
    // .omp/extensions/ per pi's extension docs; opencode's is .opencode/plugins/.
    private static readonly (string AgentId, string Resource, string RelativePath)[] Stubs =
    {
        ("opencode", "opencode-vs-diff-gate.js", Path.Combine(".opencode", "plugins", "vs-diff-gate.js")),
        ("oh-my-pi", "omp-vs-diff-gate.ts", Path.Combine(".omp", "extensions", "vs-diff-gate.ts")),
    };

    public static void EnsureInstalled(string workspaceRoot, AgentProfile? profile = null)
    {
        var agent = profile ?? AgentProfile.ClaudeCode;
        var stub = Stubs.FirstOrDefault(s => string.Equals(s.AgentId, agent.Id, StringComparison.OrdinalIgnoreCase));
        if (stub.RelativePath is null) return; // Claude Code has native hooks - no stub to deploy

        try
        {
            var dir = Path.Combine(workspaceRoot, Path.GetDirectoryName(stub.RelativePath)!);
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, Path.GetFileName(stub.RelativePath));
            var content = ReadEmbeddedStub(stub.Resource);

            // Content-checked write: don't touch an already-deployed (possibly user-edited) copy,
            // and don't re-trigger opencode's plugin watcher on every launch.
            if (File.Exists(target) &&
                string.Equals(File.ReadAllText(target), content, StringComparison.Ordinal))
            {
                Log.Info($"stub: {agent.DisplayName} diff-gate already deployed at {target}");
                return;
            }

            File.WriteAllText(target, content);
            Log.Info($"stub: deployed {agent.DisplayName} diff-gate to {target}");
        }
        catch (Exception e)
        {
            Log.Warn($"stub install failed for {agent.DisplayName}: {e.Message}");
        }
    }

    private static string ReadEmbeddedStub(string stubFileName)
    {
        var asm = typeof(AgentStubInstaller).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(stubFileName, StringComparison.OrdinalIgnoreCase));
        if (name == null)
            throw new InvalidOperationException($"embedded stub not found: {stubFileName}");

        // Guard against a duplicate match (e.g. embedded via Link with the same tail): the stub
        // names are unique (opencode-vs-diff-gate.js / omp-vs-diff-gate.ts suffix).
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}