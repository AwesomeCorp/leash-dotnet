using Leash.Api.Models;
using System.Text.Json;

namespace Leash.Api.Services.Harness;

/// <summary>
/// Claude Code client implementation.
/// Handles Claude-specific input mapping, response formatting, and transcript parsing.
/// </summary>
public class ClaudeHarnessClient : IHarnessClient
{
    private readonly string _transcriptDir;

    private static readonly HashSet<string> _passthroughTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "AskUserQuestion"
    };

    public ClaudeHarnessClient()
    {
        _transcriptDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");
    }

    public string Name => "claude";
    public string DisplayName => "Claude Code";

    // --- Hook Input/Output ---

    public HookInput MapInput(JsonElement raw, string hookEvent)
    {
        var input = new HookInput
        {
            HookEventName = hookEvent,
            Provider = Name,
            Timestamp = DateTime.UtcNow
        };

        if (raw.TryGetProperty("sessionId", out var sid))
            input.SessionId = sid.GetString() ?? string.Empty;
        else if (raw.TryGetProperty("session_id", out var sid2))
            input.SessionId = sid2.GetString() ?? string.Empty;

        if (raw.TryGetProperty("toolName", out var tn))
            input.ToolName = tn.GetString();
        else if (raw.TryGetProperty("tool_name", out var tn2))
            input.ToolName = tn2.GetString();

        if (raw.TryGetProperty("toolInput", out var ti))
            input.ToolInput = ti;
        else if (raw.TryGetProperty("tool_input", out var ti2))
            input.ToolInput = ti2;

        if (raw.TryGetProperty("cwd", out var cwd))
            input.Cwd = cwd.GetString();

        return input;
    }

    public object FormatResponse(string hookEvent, HookOutput output)
    {
        return hookEvent switch
        {
            "PermissionRequest" => FormatPermissionResponse(output),
            "PreToolUse" => FormatPreToolResponse(output),
            "PostToolUse" => FormatPostToolResponse(output),
            _ => new { }
        };
    }

    public object FormatPassthrough() => new { };

    public string NormalizeEventName(string rawEvent) => rawEvent;

    public bool IsPassthroughTool(string toolName)
        => _passthroughTools.Contains(toolName);

    // --- Transcripts ---

    public string? GetTranscriptDirectory()
        => Directory.Exists(_transcriptDir) ? _transcriptDir : null;

    public List<ClaudeProject> DiscoverProjects()
    {
        var projects = new List<ClaudeProject>();
        if (!Directory.Exists(_transcriptDir))
            return projects;

        try
        {
            foreach (var projectDir in Directory.GetDirectories(_transcriptDir))
            {
                projects.Add(new ClaudeProject
                {
                    Name = Path.GetFileName(projectDir),
                    Path = projectDir,
                    Provider = Name,
                    Sessions = GetSessionsForProject(projectDir)
                });
            }
        }
        catch { }

        return projects;
    }

    public List<ClaudeSession> GetSessionsForProject(string projectDir)
    {
        var sessions = new List<ClaudeSession>();
        try
        {
            foreach (var file in Directory.GetFiles(projectDir, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                var fileInfo = new FileInfo(file);
                sessions.Add(new ClaudeSession
                {
                    SessionId = Path.GetFileNameWithoutExtension(file),
                    FilePath = file,
                    LastModified = fileInfo.LastWriteTimeUtc,
                    SizeBytes = fileInfo.Length,
                    Provider = Name
                });
            }
        }
        catch { }

        return sessions.OrderByDescending(s => s.LastModified).ToList();
    }

    public string? FindTranscriptFile(string sessionId)
    {
        if (!Directory.Exists(_transcriptDir))
            return null;

        try
        {
            foreach (var projectDir in Directory.GetDirectories(_transcriptDir))
            {
                foreach (var file in Directory.GetFiles(projectDir, "*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    if (Path.GetFileNameWithoutExtension(file)
                        .Equals(sessionId, StringComparison.OrdinalIgnoreCase))
                        return file;
                }
            }
        }
        catch { }

        return null;
    }

    public TranscriptEntry? ParseTranscriptLine(string jsonLine)
        => JsonSerializer.Deserialize<TranscriptEntry>(jsonLine);

    // --- Settings ---

    public string? GetSettingsFilePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "settings.json");

    public string? GetDefaultPromptTemplate(string eventName)
    {
        return eventName switch
        {
            "PreToolUse" => "pre-tool-use-prompt.txt",
            "PostToolUse" => "post-tool-validation-prompt.txt",
            "PermissionRequest" => "bash-prompt.txt",
            _ => null
        };
    }

    // --- Private formatting helpers ---

    private static object FormatPermissionResponse(HookOutput output)
    {
        var behavior = output.AutoApprove ? "allow" : "deny";
        var decision = new Dictionary<string, object> { ["behavior"] = behavior };

        if (!output.AutoApprove)
        {
            var reasoning = output.Reasoning.Length > 1000
                ? output.Reasoning[..1000]
                : output.Reasoning;
            decision["message"] = $"Safety score {output.SafetyScore} below threshold {output.Threshold}. {reasoning}";
        }

        return new
        {
            hookSpecificOutput = new Dictionary<string, object>
            {
                ["hookEventName"] = "PermissionRequest",
                ["decision"] = decision
            }
        };
    }

    private static object FormatPreToolResponse(HookOutput output)
    {
        // AutoApprove flag takes precedence (set by tray user approval or handler logic)
        string permissionDecision;
        if (output.AutoApprove)
            permissionDecision = "allow";
        else if (output.SafetyScore >= output.Threshold)
            permissionDecision = "allow";
        else if (output.SafetyScore < 30)
            permissionDecision = "deny";
        else
            permissionDecision = "ask";

        var hookOutput = new Dictionary<string, object>
        {
            ["hookEventName"] = "PreToolUse",
            ["permissionDecision"] = permissionDecision
        };

        if (permissionDecision != "allow")
        {
            var reasoning = output.Reasoning.Length > 1000
                ? output.Reasoning[..1000]
                : output.Reasoning;
            hookOutput["permissionDecisionReason"] = reasoning;
        }

        return new { hookSpecificOutput = hookOutput };
    }

    private static object FormatPostToolResponse(HookOutput output)
    {
        if (!string.IsNullOrEmpty(output.SystemMessage))
        {
            return new
            {
                hookSpecificOutput = new
                {
                    hookEventName = "PostToolUse",
                    additionalContext = output.SystemMessage.Length > 500
                        ? output.SystemMessage[..500]
                        : output.SystemMessage
                }
            };
        }
        return new { };
    }
}
