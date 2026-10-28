using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using RepoNLauncher.Models;

namespace RepoNLauncher.Services;

/// <summary>
/// A Claude Code turn: claude -p with streamed JSON in and out. Permission prompts come back as control requests on
/// stdout, which the chat answers on stdin (--permission-prompt-tool stdio, the protocol the Agent SDK uses), so input
/// stays open until the turn's result arrives.
/// </summary>
public sealed class ClaudeChatTurn : AgentTurn
{
    private readonly ClaudeEventParser _parser;

    public ClaudeChatTurn(string program, ChatTurnRequest request, Action<AgentEvent> onEvent) : base("Claude Code", onEvent)
    {
        _parser = new ClaudeEventParser(request.WorkingDirectory);

        var args = new List<string>
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--permission-prompt-tool", "stdio", "--permission-mode", request.Mode,
        };
        if (request.Model.Length > 0) args.AddRange(["--model", request.Model]);
        if (request.Effort.Length > 0) args.AddRange(["--effort", request.Effort]);
        if (request.SessionId != null) args.AddRange(["--resume", request.SessionId]);

        // Attached files outside the project: let Claude read them without asking first.
        foreach (var dir in request.Attachments.Where(a => !a.IsImage).Select(a => Path.GetDirectoryName(a.Path)).OfType<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(dir => PathText.Relative(dir, request.WorkingDirectory) == dir))
            args.AddRange(["--add-dir", dir]);

        Start(AgentCli.StartInfo(program, args, request.WorkingDirectory), UserMessage(request).ToJsonString());
    }

    public override void Answer(string requestId, JsonObject response) =>
        WriteLine(new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = requestId, ["response"] = response },
        }.ToJsonString());

    protected override IEnumerable<AgentEvent> ParseLine(JsonElement line)
    {
        var events = _parser.Parse(line).ToList();
        // The turn is over; closing input lets claude exit.
        if (events.Any(e => e is TurnFinishedEvent)) CloseInput();
        return events;
    }

    // Images go inline; other files as @"path" mentions, which Claude Code reads in, as it does for typed mentions.
    private static JsonObject UserMessage(ChatTurnRequest request)
    {
        var content = new JsonArray();
        foreach (var image in request.Attachments.Where(a => a.IsImage))
        {
            if (ImageAttachments.Encode(image.Path) is not { } encoded) continue;
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = encoded.MediaType, ["data"] = encoded.Base64 },
            });
        }

        var text = request.Prompt;
        var files = request.Attachments.Where(a => !a.IsImage).Select(a => $"@\"{a.Path}\"").ToList();
        if (files.Count > 0) text = (text.Length > 0 ? text + "\n\n" : string.Empty) + string.Join(" ", files);
        if (text.Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = text });

        return new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
        };
    }
}

/// <summary>Images ready to send inline: big ones are scaled down, since the API takes up to about 5 MB and 8000 px.</summary>
public static class ImageAttachments
{
    private const long MaxBytes = 3_500_000;
    private const int MaxSide = 2000;

    public static (string MediaType, string Base64)? Encode(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        var mediaType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        };

        try
        {
            var frame = BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
            if (bytes.Length <= MaxBytes && longest <= MaxSide) return (mediaType, Convert.ToBase64String(bytes));

            var scale = Math.Min(1.0, (double)MaxSide / longest);
            var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
            encoder.Frames.Add(BitmapFrame.Create(new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale))));
            using var output = new MemoryStream();
            encoder.Save(output);
            return ("image/jpeg", Convert.ToBase64String(output.ToArray()));
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException)
        {
            // A format Windows can't decode (some WebP): send it as is if it's small enough.
            return bytes.Length <= MaxBytes ? (mediaType, Convert.ToBase64String(bytes)) : null;
        }
    }
}
