using System.Text.Json;
using System.Text.Json.Nodes;

namespace Leash.Api.Services;

/// <summary>
/// Generates and manages Copilot CLI hook scripts and hooks.json files.
/// Supports both repo-level (.github/hooks/) and user-level (~/.copilot/hooks/) installation.
/// </summary>
public class CopilotHookInstaller
{
    private readonly ILogger<CopilotHookInstaller> _logger;
    private readonly string _serviceUrl;

    private const string ScriptMarker = "# copilot-analyzer";

    private static readonly string[] CopilotEvents = { "preToolUse", "postToolUse" };

    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        WriteIndented = true
    };

    public CopilotHookInstaller(ILogger<CopilotHookInstaller> logger, string serviceUrl = "http://localhost:5050")
    {
        _logger = logger;
        _serviceUrl = serviceUrl;
    }

    /// <summary>
    /// Checks if hooks are installed at the repo level (.github/hooks/).
    /// </summary>
    public bool IsRepoInstalled(string repoPath)
    {
        var hooksJsonPath = Path.Combine(repoPath, ".github", "hooks", "hooks.json");
        return File.Exists(hooksJsonPath) && ContainsOurHooks(hooksJsonPath);
    }

    /// <summary>
    /// Checks if hooks are installed at the user level (~/.copilot/hooks/).
    /// </summary>
    public bool IsUserInstalled()
    {
        var hooksJsonPath = GetUserHooksJsonPath();
        return File.Exists(hooksJsonPath) && ContainsOurHooks(hooksJsonPath);
    }

    /// <summary>
    /// Installs Copilot hooks at the repo level.
    /// Creates .github/hooks/ with hooks.json and per-event scripts.
    /// </summary>
    public void InstallRepo(string repoPath)
    {
        var hooksDir = Path.Combine(repoPath, ".github", "hooks");
        _logger.LogInformation("Installing Copilot hooks at repo level: {Path}", hooksDir);
        InstallToDirectory(hooksDir);
    }

    /// <summary>
    /// Installs Copilot hooks for the current working directory.
    /// Creates .github/hooks/ relative to the current directory.
    /// This is the recommended way to install hooks for Copilot CLI.
    /// </summary>
    public void InstallCwd()
    {
        var hooksDir = Path.Combine(Directory.GetCurrentDirectory(), ".github", "hooks");
        _logger.LogInformation("Installing Copilot hooks in current directory: {Path}", hooksDir);
        InstallToDirectory(hooksDir);
    }

    /// <summary>
    /// Uninstalls Copilot hooks from the current working directory.
    /// </summary>
    public void UninstallCwd()
    {
        var hooksDir = Path.Combine(Directory.GetCurrentDirectory(), ".github", "hooks");
        _logger.LogInformation("Uninstalling Copilot hooks from current directory: {Path}", hooksDir);
        UninstallFromDirectory(hooksDir);
    }

    /// <summary>
    /// Installs Copilot hooks at the user level (~/.copilot/hooks/).
    /// Note: Copilot CLI only supports repo-level hooks (.github/hooks/).
    /// User-level installation is provided as a convenience but may not be picked up by Copilot CLI.
    /// Prefer InstallRepo() with the target repository path.
    /// </summary>
    public void InstallUser()
    {
        var hooksDir = GetUserHooksDir();
        _logger.LogWarning("Installing Copilot hooks at user level ({Path}). " +
            "Note: Copilot CLI only reads hooks from .github/hooks/ in the repository. " +
            "Use --copilot-repo=<path> to install at the repo level instead.", hooksDir);
        InstallToDirectory(hooksDir);
    }

    /// <summary>
    /// Uninstalls Copilot hooks from the repo level.
    /// </summary>
    public void UninstallRepo(string repoPath)
    {
        var hooksDir = Path.Combine(repoPath, ".github", "hooks");
        _logger.LogInformation("Uninstalling Copilot hooks from repo level: {Path}", hooksDir);
        UninstallFromDirectory(hooksDir);
    }

    /// <summary>
    /// Uninstalls Copilot hooks from the user level.
    /// </summary>
    public void UninstallUser()
    {
        var hooksDir = GetUserHooksDir();
        _logger.LogInformation("Uninstalling Copilot hooks from user level: {Path}", hooksDir);
        UninstallFromDirectory(hooksDir);
    }

    private void InstallToDirectory(string hooksDir)
    {
        Directory.CreateDirectory(hooksDir);

        // Generate per-event scripts
        foreach (var eventName in CopilotEvents)
        {
            WriteBashScript(hooksDir, eventName);
            WritePowerShellScript(hooksDir, eventName);
        }

        // Generate or update hooks.json
        WriteHooksJson(hooksDir);

        _logger.LogInformation("Copilot hooks installed at {Path}", hooksDir);
    }

    private void UninstallFromDirectory(string hooksDir)
    {
        if (!Directory.Exists(hooksDir))
        {
            _logger.LogDebug("Hooks directory not found, nothing to uninstall: {Path}", hooksDir);
            return;
        }

        // Remove our scripts
        foreach (var eventName in CopilotEvents)
        {
            var bashPath = Path.Combine(hooksDir, $"{eventName}.sh");
            var psPath = Path.Combine(hooksDir, $"{eventName}.ps1");

            if (File.Exists(bashPath) && File.ReadAllText(bashPath).Contains(ScriptMarker))
                File.Delete(bashPath);
            if (File.Exists(psPath) && File.ReadAllText(psPath).Contains(ScriptMarker))
                File.Delete(psPath);
        }

        // Remove our entries from hooks.json
        var hooksJsonPath = Path.Combine(hooksDir, "hooks.json");
        if (File.Exists(hooksJsonPath))
        {
            try
            {
                var json = File.ReadAllText(hooksJsonPath);
                var doc = JsonNode.Parse(json);
                if (doc is JsonObject root)
                {
                    // Copilot spec: events are nested under root["hooks"]
                    if (root["hooks"] is JsonObject hooksObj)
                    {
                        RemoveOurEntries(hooksObj);
                        if (hooksObj.Count == 0)
                            root.Remove("hooks");
                    }

                    // Also handle legacy flat format (no "hooks" wrapper)
                    RemoveOurEntries(root);

                    // Delete file if only "version" remains (or empty)
                    var hasContent = root.Any(kvp => kvp.Key != "version");
                    if (!hasContent)
                        File.Delete(hooksJsonPath);
                    else
                        File.WriteAllText(hooksJsonPath, root.ToJsonString(s_writeOptions));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up hooks.json at {Path}", hooksJsonPath);
            }
        }

        // Clean up empty directory
        if (Directory.Exists(hooksDir) && !Directory.EnumerateFileSystemEntries(hooksDir).Any())
        {
            try { Directory.Delete(hooksDir); }
            catch { /* non-fatal */ }
        }

        _logger.LogInformation("Copilot hooks uninstalled from {Path}", hooksDir);
    }

    private void WriteBashScript(string hooksDir, string eventName)
    {
        var scriptPath = Path.Combine(hooksDir, $"{eventName}.sh");
        var content =
            "#!/bin/bash\n" +
            ScriptMarker + "\n" +
            $"# Copilot CLI hook script for {eventName}\n" +
            "# Reads JSON from stdin, sends to Leash service, outputs response\n" +
            "\n" +
            "INPUT=$(cat)\n" +
            $"echo \"$INPUT\" | curl -sS -X POST \"{_serviceUrl}/api/hooks/copilot?event={eventName}\" \\\n" +
            "  -H \"Content-Type: application/json\" \\\n" +
            "  -d @- 2>/dev/null || echo '{}'\n";

        File.WriteAllText(scriptPath, content);

        // Make executable on Unix
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(scriptPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not set executable permission on {Path}", scriptPath);
            }
        }
    }

    private void WritePowerShellScript(string hooksDir, string eventName)
    {
        var scriptPath = Path.Combine(hooksDir, $"{eventName}.ps1");
        var content =
            ScriptMarker + "\n" +
            $"# Copilot CLI hook script for {eventName}\n" +
            "# Reads JSON from stdin, sends to Leash service, outputs response\n" +
            "\n" +
            "try {\n" +
            "    $inputData = [Console]::In.ReadToEnd()\n" +
            $"    $response = Invoke-RestMethod -Uri \"{_serviceUrl}/api/hooks/copilot?event={eventName}\" `\n" +
            "        -Method POST `\n" +
            "        -ContentType \"application/json\" `\n" +
            "        -Body $inputData `\n" +
            "        -ErrorAction SilentlyContinue\n" +
            "    $response | ConvertTo-Json -Compress\n" +
            "} catch {\n" +
            "    Write-Output '{}'\n" +
            "}\n";

        File.WriteAllText(scriptPath, content);
    }

    private void WriteHooksJson(string hooksDir)
    {
        var hooksJsonPath = Path.Combine(hooksDir, "hooks.json");

        JsonObject root;
        JsonObject hooksObj;
        if (File.Exists(hooksJsonPath))
        {
            try
            {
                var existing = JsonNode.Parse(File.ReadAllText(hooksJsonPath));
                root = existing?.AsObject() ?? new JsonObject();
                hooksObj = root["hooks"]?.AsObject() ?? new JsonObject();
                RemoveOurEntries(hooksObj);
            }
            catch
            {
                root = new JsonObject();
                hooksObj = new JsonObject();
            }
        }
        else
        {
            root = new JsonObject();
            hooksObj = new JsonObject();
        }

        // Add our hook entries per Copilot spec: { type, bash, powershell }
        foreach (var eventName in CopilotEvents)
        {
            var eventArray = hooksObj[eventName]?.AsArray() ?? new JsonArray();

            var bashPath = Path.Combine(hooksDir, $"{eventName}.sh").Replace('\\', '/');
            var psPath = Path.Combine(hooksDir, $"{eventName}.ps1");

            var entry = new JsonObject
            {
                ["type"] = "command",
                ["bash"] = bashPath,
                ["powershell"] = $"powershell -ExecutionPolicy Bypass -File \"{psPath}\"",
                ["description"] = $"Leash - {eventName} {ScriptMarker}"
            };

            eventArray.Add(entry);
            hooksObj[eventName] = eventArray;
        }

        root["version"] = 1;
        root["hooks"] = hooksObj;

        File.WriteAllText(hooksJsonPath, root.ToJsonString(s_writeOptions));
    }

    private bool ContainsOurHooks(string hooksJsonPath)
    {
        try
        {
            var json = File.ReadAllText(hooksJsonPath);
            return json.Contains(ScriptMarker);
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveOurEntries(JsonObject obj)
    {
        foreach (var kvp in obj.ToList())
        {
            if (kvp.Value is not JsonArray arr) continue;

            var toRemove = new List<JsonNode>();
            foreach (var entry in arr)
            {
                var desc = entry?["description"]?.GetValue<string>();
                var cmd = entry?["command"]?.GetValue<string>();
                var bash = entry?["bash"]?.GetValue<string>();
                var ps = entry?["powershell"]?.GetValue<string>();
                if ((desc != null && desc.Contains(ScriptMarker)) ||
                    (cmd != null && cmd.Contains(ScriptMarker)) ||
                    (bash != null && bash.Contains("api/hooks/copilot")) ||
                    (ps != null && ps.Contains("api/hooks/copilot")))
                {
                    if (entry != null)
                        toRemove.Add(entry);
                }
            }

            foreach (var node in toRemove)
                arr.Remove(node);

            if (arr.Count == 0)
                obj.Remove(kvp.Key);
        }
    }

    private static string GetUserHooksDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "hooks");
    }

    private static string GetUserHooksJsonPath()
    {
        return Path.Combine(GetUserHooksDir(), "hooks.json");
    }
}
