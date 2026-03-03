using Leash.Api.Services.Harness;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Leash.Api.Services;

public class TranscriptWatcher : IDisposable
{
    private readonly HarnessClientRegistry _clientRegistry;
    private readonly ILogger<TranscriptWatcher> _logger;
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, long> _filePositions = new();
    private readonly ConcurrentDictionary<string, IHarnessClient> _dirToClient = new();
    private int _disposed;

    public event EventHandler<TranscriptEventArgs>? TranscriptUpdated;

    public TranscriptWatcher(HarnessClientRegistry clientRegistry, ILogger<TranscriptWatcher> logger)
    {
        _clientRegistry = clientRegistry;
        _logger = logger;
    }

    public void Start()
    {
        foreach (var client in _clientRegistry.GetAll())
        {
            var dir = client.GetTranscriptDirectory();
            if (dir == null) continue;

            _dirToClient[dir] = client;
            _logger.LogDebug("Starting transcript watcher for {Client} at {Dir}", client.DisplayName, dir);

            if (client.Name == "claude")
                StartClaudeWatcher(dir);
            else if (client.Name == "copilot")
                StartCopilotWatcher(dir);
        }
    }

    private void StartClaudeWatcher(string projectsDir)
    {

        try
        {
            var topWatcher = new FileSystemWatcher(projectsDir)
            {
                NotifyFilter = NotifyFilters.DirectoryName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            topWatcher.Created += OnProjectDirectoryCreated;
            _watchers["__top__"] = topWatcher;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to watch top-level projects directory");
        }

        // Set up watchers for existing project directories
        try
        {
            foreach (var projectDir in Directory.GetDirectories(projectsDir))
                WatchProjectDirectory(projectDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate project directories");
        }
    }

    private void StartCopilotWatcher(string sessionDir)
    {

        // Watch for new session folders
        try
        {
            var topWatcher = new FileSystemWatcher(sessionDir)
            {
                NotifyFilter = NotifyFilters.DirectoryName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            topWatcher.Created += OnCopilotSessionCreated;
            _watchers["__copilot_top__"] = topWatcher;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to watch Copilot session-state directory");
        }

        // Watch existing session folders for events.jsonl changes
        try
        {
            foreach (var dir in Directory.GetDirectories(sessionDir))
                WatchCopilotSessionDirectory(dir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate Copilot session directories");
        }
    }

    public List<ClaudeProject> GetProjects()
    {
        var allProjects = new List<ClaudeProject>();
        foreach (var client in _clientRegistry.GetAll())
            allProjects.AddRange(client.DiscoverProjects());

        return MergeProjectsByFolder(allProjects);
    }

    /// <summary>
    /// Merges projects from different clients that share the same working directory.
    /// </summary>
    private static List<ClaudeProject> MergeProjectsByFolder(List<ClaudeProject> projects)
    {
        var byFolder = new Dictionary<string, ClaudeProject>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
        {
            // Resolve the actual cwd for grouping
            var cwd = project.Cwd;
            if (string.IsNullOrEmpty(cwd) && project.Provider == "claude")
                cwd = DecodeClaudeProjectPath(project.Name);

            if (string.IsNullOrEmpty(cwd))
                cwd = project.Path;

            var key = cwd.TrimEnd('\\', '/');

            if (byFolder.TryGetValue(key, out var existing))
            {
                // Merge sessions into existing project
                existing.Sessions.AddRange(project.Sessions);

                // Fill in git info from whichever source has it
                existing.GitRoot ??= project.GitRoot;
                existing.Branch ??= project.Branch;
                existing.Repository ??= project.Repository;
            }
            else
            {
                var folderName = System.IO.Path.GetFileName(key);
                byFolder[key] = new ClaudeProject
                {
                    Name = string.IsNullOrEmpty(folderName) ? key : folderName,
                    Path = project.Path,
                    Provider = project.Provider,
                    Cwd = cwd,
                    GitRoot = project.GitRoot,
                    Branch = project.Branch,
                    Repository = project.Repository,
                    Sessions = new List<ClaudeSession>(project.Sessions)
                };
            }
        }

        // Sort sessions within each merged project by last modified descending
        foreach (var project in byFolder.Values)
            project.Sessions = project.Sessions.OrderByDescending(s => s.LastModified).ToList();

        return byFolder.Values.ToList();
    }

    /// <summary>
    /// Decodes a Claude project directory name back to the original filesystem path.
    /// E.g. "C--Users-shahabm-source-repos-ClaudeObserver" → "C:\Users\shahabm\source\repos\ClaudeObserver"
    /// </summary>
    public static string DecodeClaudeProjectPath(string encoded)
    {
        if (string.IsNullOrEmpty(encoded))
            return encoded;

        // Pattern: drive letter followed by -- (e.g. "C--")
        if (encoded.Length >= 3 && char.IsLetter(encoded[0]) && encoded[1] == '-' && encoded[2] == '-')
        {
            return encoded[0] + @":\" + encoded[3..].Replace('-', '\\');
        }

        // Fallback: just replace dashes with separators
        return encoded.Replace('-', System.IO.Path.DirectorySeparatorChar);
    }
    public List<TranscriptEntry> GetTranscript(string sessionId)
    {
        var entries = new List<TranscriptEntry>();
        var (file, client) = FindTranscriptFileWithClient(sessionId);

        if (file == null || client == null || !File.Exists(file))
            return entries;

        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var entry = client.ParseTranscriptLine(line);
                    if (entry != null)
                        entries.Add(entry);
                }
                catch (JsonException)
                {
                    // Skip malformed lines
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read transcript for session {SessionId}", sessionId);
        }

        return entries;
    }

    public string? FindTranscriptFile(string sessionId)
    {
        var (file, _) = FindTranscriptFileWithClient(sessionId);
        return file;
    }

    private (string? file, IHarnessClient? client) FindTranscriptFileWithClient(string sessionId)
    {
        foreach (var client in _clientRegistry.GetAll())
        {
            var file = client.FindTranscriptFile(sessionId);
            if (file != null)
                return (file, client);
        }
        return (null, null);
    }

    private void WatchProjectDirectory(string projectDir)
    {
        if (_watchers.ContainsKey(projectDir))
            return;

        try
        {
            var watcher = new FileSystemWatcher(projectDir, "*.jsonl")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            watcher.Changed += OnTranscriptFileChanged;
            watcher.Created += OnTranscriptFileChanged;

            _watchers[projectDir] = watcher;
            _logger.LogDebug("Watching for transcript changes in {Dir}", projectDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to watch project directory {Dir}", projectDir);
        }
    }

    private void OnProjectDirectoryCreated(object sender, FileSystemEventArgs e)
    {
        if (Directory.Exists(e.FullPath))
        {
            WatchProjectDirectory(e.FullPath);
        }
    }

    private void OnTranscriptFileChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            var client = ResolveClientForPath(e.FullPath);
            var newEntries = ReadNewEntries(e.FullPath, client);

            if (newEntries.Count > 0)
            {
                // For Claude, sessionId is filename; for Copilot, it's the parent folder
                var sessionId = client?.Name == "copilot"
                    ? Path.GetFileName(Path.GetDirectoryName(e.FullPath) ?? "")
                    : Path.GetFileNameWithoutExtension(e.FullPath);

                TranscriptUpdated?.Invoke(this, new TranscriptEventArgs
                {
                    SessionId = sessionId,
                    NewEntries = newEntries
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error processing transcript file change: {File}", e.FullPath);
        }
    }

    private IHarnessClient? ResolveClientForPath(string filePath)
    {
        foreach (var (dir, client) in _dirToClient)
        {
            if (filePath.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                return client;
        }
        return _clientRegistry.Get("claude"); // fallback
    }

    private List<TranscriptEntry> ReadNewEntries(string filePath, IHarnessClient? client)
    {
        var entries = new List<TranscriptEntry>();
        client ??= _clientRegistry.Get("claude");

        var lastPos = _filePositions.GetOrAdd(filePath, 0);

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= lastPos)
                return entries;

            stream.Seek(lastPos, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var entry = client?.ParseTranscriptLine(line);
                    if (entry != null)
                        entries.Add(entry);
                }
                catch (JsonException)
                {
                    // Skip malformed lines
                }
            }

            _filePositions[filePath] = stream.Position;
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not read new entries from {File}", filePath);
        }

        return entries;
    }

    // --- Copilot watcher helpers (kept here because FileSystemWatcher setup is orchestrator-level) ---

    private void WatchCopilotSessionDirectory(string sessionDir)
    {
        var watchKey = "copilot:" + sessionDir;
        if (_watchers.ContainsKey(watchKey))
            return;

        var eventsFile = Path.Combine(sessionDir, "events.jsonl");
        if (!File.Exists(eventsFile)) return;

        try
        {
            var watcher = new FileSystemWatcher(sessionDir, "events.jsonl")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            watcher.Changed += OnTranscriptFileChanged;

            _watchers[watchKey] = watcher;
            _logger.LogDebug("Watching Copilot session {Dir}", sessionDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to watch Copilot session directory {Dir}", sessionDir);
        }
    }

    private void OnCopilotSessionCreated(object sender, FileSystemEventArgs e)
    {
        if (Directory.Exists(e.FullPath))
        {
            WatchCopilotSessionDirectory(e.FullPath);
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;

        foreach (var watcher in _watchers.Values)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing file watcher");
            }
        }

        _watchers.Clear();
        GC.SuppressFinalize(this);
    }
}

public class TranscriptEventArgs : EventArgs
{
    public string SessionId { get; set; } = string.Empty;
    public List<TranscriptEntry> NewEntries { get; set; } = new();
}

public class TranscriptEntry
{
    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string? Type { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("uuid")]
    public string? Uuid { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("parentUuid")]
    public string? ParentUuid { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("version")]
    public string? Version { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("cwd")]
    public string? Cwd { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public JsonElement? Message { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public JsonElement? Data { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("provider")]
    public string? Provider { get; set; }

    /// <summary>
    /// Extracts a display-friendly summary of the message content.
    /// </summary>
    public string? GetMessageSummary()
    {
        if (Message == null || Message.Value.ValueKind == JsonValueKind.Undefined)
            return null;

        try
        {
            var msg = Message.Value;
            // User/assistant messages have { role, content }
            if (msg.ValueKind == JsonValueKind.Object)
            {
                if (msg.TryGetProperty("content", out var content))
                {
                    if (content.ValueKind == JsonValueKind.String)
                        return content.GetString();
                    if (content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0)
                    {
                        var first = content[0];
                        if (first.TryGetProperty("text", out var text))
                            return text.GetString();
                    }
                }
                if (msg.TryGetProperty("role", out var role))
                    return $"[{role.GetString()}]";
            }
            if (msg.ValueKind == JsonValueKind.String)
                return msg.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Extracts role from message if present.
    /// </summary>
    public string? GetRole()
    {
        if (Message == null || Message.Value.ValueKind != JsonValueKind.Object)
            return null;
        try
        {
            if (Message.Value.TryGetProperty("role", out var role))
                return role.GetString();
        }
        catch { }
        return null;
    }
}

public class ClaudeProject
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Provider { get; set; } = "claude";
    public string? Cwd { get; set; }
    public string? GitRoot { get; set; }
    public string? Branch { get; set; }
    public string? Repository { get; set; }
    public List<ClaudeSession> Sessions { get; set; } = new();
}

public class ClaudeSession
{
    public string SessionId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime LastModified { get; set; }
    public long SizeBytes { get; set; }
    public string Provider { get; set; } = "claude";
    public string? Cwd { get; set; }
    public string? GitRoot { get; set; }
    public string? Branch { get; set; }
    public string? Repository { get; set; }
}
