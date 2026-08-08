using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PLog = ClaudeCodeVs.Protocol.Log;

namespace ClaudeCodeVs;

/// <summary>
/// Parses the Claude Code conversation transcript (a JSONL the CLI writes) to aggregate token usage and
/// an estimated cost for the session, which the dockable panel shows. The Stop hook hands us the
/// transcript path via POST /usage. The IDE protocol exposes none of this - the transcript is the only
/// source. Both the format and the prices are undocumented/version-fragile, so parse defensively and
/// label the cost an estimate.
/// </summary>
internal static class UsageTracker
{
    /// <summary>USD per 1,000,000 tokens. Approx public list prices as of 2026-01; cost is an estimate.</summary>
    private readonly struct Price
    {
        public Price(double input, double output, double cacheWrite, double cacheRead)
        { Input = input; Output = output; CacheWrite = cacheWrite; CacheRead = cacheRead; }
        public double Input { get; }
        public double Output { get; }
        public double CacheWrite { get; }
        public double CacheRead { get; }
    }

    private static Price PriceFor(string? model)
    {
        var m = (model ?? string.Empty).ToLowerInvariant();
        if (m.Contains("opus")) return new Price(15.0, 75.0, 18.75, 1.50);
        if (m.Contains("haiku")) return new Price(1.0, 5.0, 1.25, 0.10);
        return new Price(3.0, 15.0, 3.75, 0.30); // default: Sonnet tier
    }

    public static async Task UpdateFromTranscriptAsync(string transcriptPath, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(transcriptPath)) return;

            // Copy the text out under a shared read lock (the CLI is still writing it).
            string text;
            using (var fs = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
                text = await sr.ReadToEndAsync();

            long input = 0, output = 0, cacheRead = 0;
            int turns = 0;
            double cost = 0;
            string? model = null;
            long lastIn = 0, lastOut = 0, lastCacheRead = 0;
            double lastCost = 0;

            // Breakdown (ROADMAP "per-tool / per-subagent"): subagent split is EXACT (sidechain
            // messages carry real usage records); per-tool context cost is an ESTIMATE (no real
            // per-call numbers exist anywhere - group tool_result sizes by tool name at ~4 chars/tok).
            long subIn = 0, subOut = 0;
            int subTurns = 0;
            var toolNameById = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            var toolChars = new System.Collections.Generic.Dictionary<string, long>(StringComparer.Ordinal);

            foreach (var line in text.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JObject o;
                try { o = JObject.Parse(line); } catch { continue; }

                bool sidechain = (bool?)o["isSidechain"] == true;
                var msgAny = o["message"] as JObject;

                // tool_use blocks (assistant): remember id -> tool name for the result pass.
                // tool_result blocks (user): attribute the result's size to that tool.
                if (msgAny?["content"] is JArray content)
                {
                    foreach (var block in content.OfType<JObject>())
                    {
                        var bt = (string?)block["type"];
                        if (bt == "tool_use" && (string?)block["id"] is string id && (string?)block["name"] is string name)
                            toolNameById[id] = name;
                        else if (bt == "tool_result" && (string?)block["tool_use_id"] is string tid
                                 && toolNameById.TryGetValue(tid, out var tname))
                        {
                            long chars = (block["content"]?.ToString() ?? "").Length;
                            toolChars.TryGetValue(tname, out long cur);
                            toolChars[tname] = cur + chars;
                        }
                    }
                }

                if ((string?)o["type"] != "assistant") continue;

                var msg = msgAny;
                if (msg?["usage"] is not JObject usage) continue;

                if (sidechain)
                {
                    subTurns++;
                    subIn += ((long?)usage["input_tokens"] ?? 0) + ((long?)usage["cache_creation_input_tokens"] ?? 0);
                    subOut += (long?)usage["output_tokens"] ?? 0;
                }

                var lineModel = (string?)msg["model"];
                if (!string.IsNullOrEmpty(lineModel)) model = lineModel;

                long i = (long?)usage["input_tokens"] ?? 0;
                long ou = (long?)usage["output_tokens"] ?? 0;
                long cr = (long?)usage["cache_read_input_tokens"] ?? 0;
                long cc = (long?)usage["cache_creation_input_tokens"] ?? 0;

                var p = PriceFor(lineModel ?? model);
                var entryCost = (i * p.Input + ou * p.Output + cc * p.CacheWrite + cr * p.CacheRead) / 1_000_000.0;

                // "Input" = freshly processed input this call: the uncached delta (input_tokens, ~1 when
                // caching) PLUS newly cached tokens (cache_creation). cache_read is the reused bulk,
                // shown separately as "cached". (Showing input_tokens alone reads as a misleading "1".)
                long freshInput = i + cc;

                // Cumulative session totals…
                input += freshInput; output += ou; cacheRead += cr; cost += entryCost; turns++;
                // …and the most recent call (overwritten each iteration -> ends on the last).
                lastIn = freshInput; lastOut = ou; lastCacheRead = cr; lastCost = entryCost;
            }

            // One compact breakdown line for the panel: exact subagent split + estimated top context consumers.
            string? breakdown = null;
            var parts = new System.Collections.Generic.List<string>();
            if (subTurns > 0)
                parts.Add($"Subagents (exact): {subTurns} calls · ↑ {Fmt(subIn)} ↓ {Fmt(subOut)}");
            var top = toolChars.OrderByDescending(kv => kv.Value).Take(4)
                .Select(kv => $"{kv.Key} ≈{Fmt(kv.Value / 4)}").ToList();
            if (top.Count > 0)
                parts.Add("Top tools (est.): " + string.Join(" · ", top));
            if (parts.Count > 0)
                breakdown = string.Join("      ", parts);

            Ui.BridgeStatus.SetUsage(
                session: new Ui.BridgeStatus.Usage(input, output, cacheRead, cost),
                latest: new Ui.BridgeStatus.Usage(lastIn, lastOut, lastCacheRead, lastCost),
                turns, model, breakdown);
            PLog.Info($"usage: latest {lastIn}/{lastOut} · session {input} in / {output} out / {cacheRead} cached, {turns} turns, ~${cost:0.00} est");
        }
        catch (Exception e)
        {
            PLog.Warn($"usage parse failed: {e.Message}");
        }
    }

    /// <summary>Compact token count: 950 -> "950", 12300 -> "12.3k".</summary>
    private static string Fmt(long tokens)
        => tokens >= 1000 ? (tokens / 1000.0).ToString("0.#") + "k" : tokens.ToString();
}
