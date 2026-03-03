using Leash.Api.Models;
using System.Text.Json;

namespace Leash.Api.Services.Harness;

/// <summary>
/// GitHub Copilot CLI client implementation.
/// Handles Copilot-specific input mapping, response formatting, and transcript parsing.
/// </summary>
public class CopilotHarnessClient : IHarnessClient
{
    private readonly string _transcriptDir;

    public CopilotHarnessClient()
    {
        _transcriptDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "session-state");
    }

    public string Name => "copilot";
    public string DisplayName => "GitHub Copilot CLI";

    // --- Hook Input/Output ---

    public HookInput MapInput(JsonElement raw, string hookEvent)
    {
        var normalizedEvent = NormalizeEventName(hookEvent);
        var input = new HookInput
        {
            HookEventName = normalizedEvent,
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

        // Copilot sends toolArgs as a JSON string
        if (raw.TryGetProperty("toolArgs", out var ta))
        {
            if (ta.ValueKind == JsonValueKind.String)
            {
                var argsStr = ta.GetString();
                if (!string.IsNullOrEmpty(argsStr))
                {
                    try
                    {
                        input.ToolInput = JsonDocument.Parse(argsStr).RootElement;
                    }
                    catch (JsonException)
                    {
                        input.ToolInput = JsonDocument.Parse(
                            $"{{\"command\":\"{argsStr.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"}}").RootElement;
                    }
                }
            }
            else
            {
                input.ToolInput = ta;
            }
        }
        else if (raw.TryGetProperty("toolInput", out var ti))
        {
            input.ToolInput = ti;
        }

        if (raw.TryGetProperty("cwd", out var cwd))
            input.Cwd = cwd.GetString();

        // Copilot may send epoch milliseconds
        if (raw.TryGetProperty("timestamp", out var ts))
        {
            if (ts.ValueKind == JsonValueKind.Number && ts.TryGetInt64(out var epochMs))
            {
                input.Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime;
            }
        }

        return input;
    }

    public object FormatResponse(string hookEvent, HookOutput output)
    {
        var normalized = NormalizeEventName(hookEvent);
        if (normalized == "PreToolUse")
        {
            // Copilot CLI only supports "allow" or "deny" (no "ask")
            // AutoApprove flag takes precedence (set by tray user approval or handler logic)
            var decision = output.AutoApprove ? "allow"
                : output.SafetyScore >= output.Threshold ? "allow"
                : "deny";

            var response = new Dictionary<string, object>
            {
                ["permissionDecision"] = decision
            };

            if (decision != "allow" && !string.IsNullOrEmpty(output.Reasoning))
            {
                var reasoning = output.Reasoning.Length > 1000
                    ? output.Reasoning[..1000]
                    : output.Reasoning;
                response["message"] = reasoning;
            }

            return response;
        }

        return new { };
    }

    public object FormatPassthrough() => new { };

    public string NormalizeEventName(string rawEvent)
    {
        return rawEvent switch
        {
            "preToolUse" => "PreToolUse",
            "postToolUse" => "PostToolUse",
            "preToolUseFailure" => "PreToolUse",
            "postToolUseFailure" => "PostToolUseFailure",
            _ => char.ToUpperInvariant(rawEvent[0]) + rawEvent[1..]
        };
    }

    public bool IsPassthroughTool(string toolName) => false;

    // --- Transcripts ---

    public string? GetTranscriptDirectory()
        => Directory.Exists(_transcriptDir) ? _transcriptDir : null;

    public List<ClaudeProject> DiscoverProjects()
    {
        var projects = new List<ClaudeProject>();
        if (!Directory.Exists(_transcriptDir))
            return projects;

        var sessions = GetCopilotSessions();
        if (sessions.Count == 0)
            return projects;

        // Group sessions by cwd (working directory)
        var byCwd = new Dictionary<string, List<ClaudeSession>>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in sessions)
        {
            var key = session.Cwd ?? "Unknown";
            if (!byCwd.ContainsKey(key))
                byCwd[key] = new List<ClaudeSession>();
            byCwd[key].Add(session);
        }

        foreach (var (cwd, groupSessions) in byCwd)
        {
            // Use the first session with git info to populate project-level fields
            var withGit = groupSessions.FirstOrDefault(s => !string.IsNullOrEmpty(s.GitRoot))
                          ?? groupSessions[0];

            var folderName = cwd == "Unknown"
                ? "Unknown"
                : Path.GetFileName(cwd.TrimEnd('\\', '/'));

            projects.Add(new ClaudeProject
            {
                Name = string.IsNullOrEmpty(folderName) ? cwd : folderName,
                Path = cwd == "Unknown" ? _transcriptDir : cwd,
                Provider = Name,
                Cwd = cwd == "Unknown" ? null : cwd,
                GitRoot = withGit.GitRoot,
                Branch = withGit.Branch,
                Repository = withGit.Repository,
                Sessions = groupSessions.OrderByDescending(s => s.LastModified).ToList()
            });
        }

        return projects;
    }

    public List<ClaudeSession> GetSessionsForProject(string projectPath)
    {
        // For Copilot, sessions are directories under the session-state root
        return GetCopilotSessions();
    }

    public string? FindTranscriptFile(string sessionId)
    {
        if (!Directory.Exists(_transcriptDir))
            return null;

        var eventsFile = Path.Combine(_transcriptDir, sessionId, "events.jsonl");
        return File.Exists(eventsFile) ? eventsFile : null;
    }

    public TranscriptEntry? ParseTranscriptLine(string jsonLine)
    {
        using var doc = JsonDocument.Parse(jsonLine);
        var root = doc.RootElement;

        var entry = new TranscriptEntry
        {
            Provider = Name,
            Type = root.TryGetProperty("type", out var t) ? t.GetString() : null,
            Uuid = root.TryGetProperty("id", out var id) ? id.GetString() : null,
            ParentUuid = root.TryGetProperty("parentId", out var pid) ? pid.GetString() : null,
            Timestamp = root.TryGetProperty("timestamp", out var ts) ? ts.GetString() : null,
        };

        if (root.TryGetProperty("data", out var data))
            entry.Data = data.Clone();

        // Map Copilot data into Message for display compatibility
        if (entry.Type == "user.message" && entry.Data?.TryGetProperty("content", out var content) == true)
        {
            entry.Message = JsonDocument.Parse(
                $"{{\"role\":\"user\",\"content\":{JsonSerializer.Serialize(content.GetString())}}}").RootElement;
        }
        else if (entry.Type == "assistant.message" && entry.Data?.TryGetProperty("content", out var aContent) == true)
        {
            entry.Message = JsonDocument.Parse(
                $"{{\"role\":\"assistant\",\"content\":{JsonSerializer.Serialize(aContent.GetString())}}}").RootElement;
        }

        return entry;
    }

    // --- Settings ---

    public string? GetSettingsFilePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "hooks", "hooks.json");

    public string? GetDefaultPromptTemplate(string eventName)
    {
        // Copilot uses the same prompt templates as Claude by default
        return eventName switch
        {
            "PreToolUse" => "pre-tool-use-prompt.txt",
            "PostToolUse" => "post-tool-validation-prompt.txt",
            _ => null
        };
    }

    // --- Private helpers ---

    private List<ClaudeSession> GetCopilotSessions()
    {
        var sessions = new List<ClaudeSession>();
        if (!Directory.Exists(_transcriptDir))
            return sessions;

        try
        {
            foreach (var sessionDir in Directory.GetDirectories(_transcriptDir))
            {
                var eventsFile = Path.Combine(sessionDir, "events.jsonl");
                if (!File.Exists(eventsFile)) continue;

                try
                {
                    var fileInfo = new FileInfo(eventsFile);
                    var session = new ClaudeSession
                    {
                        SessionId = Path.GetFileName(sessionDir),
                        FilePath = eventsFile,
                        LastModified = fileInfo.LastWriteTimeUtc,
                        SizeBytes = fileInfo.Length,
                        Provider = Name
                    };

                    // Read the first line to extract session.start metadata
                    ReadSessionStartMetadata(eventsFile, session);

                    sessions.Add(session);
                }
                catch { }
            }
        }
        catch { }

        return sessions.OrderByDescending(s => s.LastModified).ToList();
    }

    /// <summary>
    /// Reads the first line of an events.jsonl file to extract session.start context.
    /// </summary>
    private static void ReadSessionStartMetadata(string eventsFile, ClaudeSession session)
    {
        try
        {
            using var stream = new FileStream(eventsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var firstLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(firstLine)) return;

            using var doc = JsonDocument.Parse(firstLine);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var type) && type.GetString() == "session.start"
                && root.TryGetProperty("data", out var data)
                && data.TryGetProperty("context", out var ctx))
            {
                if (ctx.TryGetProperty("cwd", out var cwd))
                    session.Cwd = cwd.GetString();
                if (ctx.TryGetProperty("gitRoot", out var gitRoot))
                    session.GitRoot = gitRoot.GetString();
                if (ctx.TryGetProperty("branch", out var branch))
                    session.Branch = branch.GetString();
                if (ctx.TryGetProperty("repository", out var repo))
                    session.Repository = repo.GetString();
            }
        }
        catch { }
    }
}
