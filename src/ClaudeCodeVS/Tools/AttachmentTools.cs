using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Attachments;
using ClaudeCodeVs.Protocol;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Tools;

/// <summary>
/// vs_list_attachments — the staged attachment tray (drop/paste/capture chips) as a pull list:
/// mention path (what the panel @-mentions), on-disk path, kind, and estimated Read cost. Lets
/// agents without an IDE WebSocket (Oh My Pi over its .mcp.json pull channel) SEE what the user
/// staged without the at_mentioned push, then read the paths with their native Read tool.
/// </summary>
internal sealed class VsListAttachmentsTool : IIdeTool
{
    public string Name => "vs_list_attachments";
    public string Description =>
        "List the files the user staged in the Visual Studio attachment tray (dropped, pasted, or "
        + "captured while debugging). Returns each as {mentionPath, path, fileName, isImage, "
        + "estTokens, needsTool}. Read a file by its on-disk path with your native read tool. "
        + "Usually empty until the user attaches something; nothing to clear.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject(),
    };

    public Task<object> InvokeAsync(JToken arguments, CancellationToken ct)
    {
        var items = AttachmentService.Snapshot()
            .Select(a => new JObject
            {
                ["mentionPath"] = a.MentionPath,
                ["path"] = a.FullPath,
                ["fileName"] = a.FileName,
                ["isImage"] = a.IsImage,
                ["estTokens"] = a.EstTokens,
                ["needsTool"] = a.NeedsTool,
            })
            .ToList();
        var result = new JObject
        {
            ["count"] = items.Count,
            ["attachments"] = new JArray(items),
        };
        Log.Info($"vs_list_attachments -> {items.Count} staged");
        return Task.FromResult<object>(result);
    }
}