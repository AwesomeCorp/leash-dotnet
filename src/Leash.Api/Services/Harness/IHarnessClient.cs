using Leash.Api.Models;
using System.Text.Json;

namespace Leash.Api.Services.Harness;

/// <summary>
/// Abstraction for an AI coding assistant client (Claude Code, GitHub Copilot CLI, etc.).
/// Encapsulates client-specific behavior: input/output formats, transcript parsing,
/// settings paths, and event naming conventions.
/// </summary>
public interface IHarnessClient
{
    /// <summary>Machine-readable name, e.g. "claude", "copilot".</summary>
    string Name { get; }

    /// <summary>Human-readable display name, e.g. "Claude Code", "GitHub Copilot CLI".</summary>
    string DisplayName { get; }

    // --- Hook Input/Output ---

    /// <summary>Maps raw JSON from the client's hook into a normalized HookInput.</summary>
    HookInput MapInput(JsonElement rawInput, string hookEvent);

    /// <summary>Formats a HookOutput into the JSON structure the client expects.</summary>
    object FormatResponse(string hookEvent, HookOutput output);

    /// <summary>Returns the empty/passthrough response for this client (no opinion).</summary>
    object FormatPassthrough();

    /// <summary>Normalizes client-specific event names to internal PascalCase format.</summary>
    string NormalizeEventName(string rawEvent);

    /// <summary>Returns true if the tool should skip analysis (non-actionable).</summary>
    bool IsPassthroughTool(string toolName);

    // --- Transcripts ---

    /// <summary>Root directory where this client stores transcripts, or null if unsupported.</summary>
    string? GetTranscriptDirectory();

    /// <summary>Discovers projects/sessions from the transcript directory.</summary>
    List<ClaudeProject> DiscoverProjects();

    /// <summary>Lists sessions within a project directory.</summary>
    List<ClaudeSession> GetSessionsForProject(string projectPath);

    /// <summary>Finds the transcript file for a given session ID, or null.</summary>
    string? FindTranscriptFile(string sessionId);

    /// <summary>Parses a single JSONL line into a TranscriptEntry.</summary>
    TranscriptEntry? ParseTranscriptLine(string jsonLine);

    // --- Settings & Configuration ---

    /// <summary>Path to the client's settings/hooks file, or null.</summary>
    string? GetSettingsFilePath();

    /// <summary>Returns the default prompt template name for a given hook event, or null.</summary>
    string? GetDefaultPromptTemplate(string eventName);
}
