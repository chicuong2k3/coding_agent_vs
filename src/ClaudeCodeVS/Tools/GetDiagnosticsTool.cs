using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Editor;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Tools;

/// <summary>
/// getDiagnostics - language/build diagnostics from the VS Error List, grouped per file. Always
/// returns the envelope [{uri, diagnostics:[...]}] (empty array if none), per build-plan §4. When a
/// uri is supplied we filter to that file and still return its (possibly empty) envelope.
/// </summary>
internal sealed class GetDiagnosticsTool : IIdeTool
{
    public string Name => "getDiagnostics";
    public string Description => "Get compiler/build diagnostics (errors and warnings) from Visual Studio's Error List, optionally filtered to a single file URI. Returns [{uri, diagnostics:[...]}].";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject { ["uri"] = new JObject { ["type"] = "string" } },
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        string? uriFilter = (string?)args["uri"];
        string? pathFilter = TryUriToPath(uriFilter);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var byFile = ErrorListReader.Read();

        // Upgrade C#/VB entries to Roslyn-precise spans (ROADMAP Phase 2): for every file the Error
        // List flagged - plus the explicitly requested file - ask the semantic model for real
        // start/end ranges. Files Roslyn doesn't know (C++, loose files) keep their point ranges,
        // and a file whose Roslyn model is clean keeps its Error List entries (build-only errors).
        try
        {
            var wanted = new List<string>(byFile.Keys);
            if (pathFilter is not null && !wanted.Any(f => PathEquals(f, pathFilter)))
                wanted.Add(pathFilter);
            var precise = await CodeModel.RoslynReader.GetPreciseDiagnosticsAsync(wanted, ct);
            if (precise is not null)
                foreach (var kv in precise)
                {
                    // MERGE, don't replace: a file's Error List array can hold entries Roslyn's semantic
                    // model doesn't produce (MSBuild build errors, Error-List-only analyzers). Keep those,
                    // but drop the Error List twins of the Roslyn diagnostics (same line + same message -
                    // Roslyn pushed them into the Error List in the first place) so nothing doubles up.
                    if (byFile.TryGetValue(kv.Key, out var errorListDiags))
                    {
                        var seen = new HashSet<string>(kv.Value
                            .Select(d => (int?)d["range"]?["start"]?["line"] + "|" + (string?)d["message"]));
                        foreach (var d in errorListDiags)
                        {
                            var key = (int?)d["range"]?["start"]?["line"] + "|" + (string?)d["message"];
                            if (!seen.Contains(key)) kv.Value.Add(d);
                        }
                    }
                    byFile[kv.Key] = kv.Value;
                }
        }
        catch (Exception e)
        {
            Log.Warn($"getDiagnostics: precise-span upgrade failed, using Error List ranges ({e.Message})");
        }

        var result = new JArray();
        bool matchedFilter = false;

        foreach (var kv in byFile)
        {
            if (pathFilter is not null && !PathEquals(kv.Key, pathFilter))
                continue;
            matchedFilter = true;
            result.Add(new JObject
            {
                ["uri"] = PathToUri(kv.Key),
                ["diagnostics"] = kv.Value,
            });
        }

        // A specific file with no diagnostics still gets an (empty) envelope.
        if (pathFilter is not null && !matchedFilter)
        {
            result.Add(new JObject
            {
                ["uri"] = uriFilter ?? PathToUri(pathFilter),
                ["diagnostics"] = new JArray(),
            });
        }

        Log.Info($"getDiagnostics: uri={uriFilter ?? "(all)"} -> {result.Count} file(s)");
        return result;
    }

    private static string? TryUriToPath(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        try { return uri!.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(uri).LocalPath : uri; }
        catch { return uri; }
    }

    private static string PathToUri(string path)
    {
        try { return new Uri(path).AbsoluteUri; } catch { return path; }
    }

    private static bool PathEquals(string a, string b)
        => string.Equals(
            a.Replace('/', '\\').TrimEnd('\\'),
            b.Replace('/', '\\').TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
}
