using Leash.Api.Models;
using Leash.Api.Exceptions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Leash.Api.Services;

public class ConfigurationManager
{
    private readonly string _configPath;
    private Configuration _configuration;
    private readonly ILogger<ConfigurationManager>? _logger;

    // Cached serializer options to avoid repeated allocation
    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions s_readOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ConfigurationManager(string? configPath = null, ILogger<ConfigurationManager>? logger = null)
    {
        _configPath = configPath ?? GetDefaultConfigPath();
        _configuration = CreateDefaultConfiguration();
        _logger = logger;
    }

    public ConfigurationManager(Configuration configuration, ILogger<ConfigurationManager>? logger = null)
    {
        _configPath = string.Empty;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<Configuration> LoadAsync()
    {
        if (File.Exists(_configPath))
        {
            try
            {
                // Use async stream reading for better memory efficiency
                await using var stream = new FileStream(_configPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                var deserialized = await JsonSerializer.DeserializeAsync<Configuration>(stream, s_readOptions).ConfigureAwait(false);

                if (deserialized == null)
                {
                    _logger?.LogError("Configuration file {ConfigPath} deserialized to null - file corrupted", _configPath);
                    throw new ConfigurationException(
                        $"Cannot load configuration: File is corrupted or empty. " +
                        $"Please fix or delete the file to regenerate defaults. Path: {_configPath}");
                }

                _configuration = deserialized;
            }
            catch (JsonException ex)
            {
                _logger?.LogError(ex, "Failed to parse configuration file {ConfigPath} - invalid JSON format", _configPath);
                throw new ConfigurationException(
                    $"Cannot load configuration: File is corrupted or invalid JSON. " +
                    $"Please fix or delete the file to regenerate defaults. Path: {_configPath}", ex);
            }
            catch (IOException ex)
            {
                _logger?.LogError(ex, "Failed to read configuration file {ConfigPath}", _configPath);
                throw new ConfigurationException($"Cannot load configuration from {_configPath}", ex);
            }
        }
        else
        {
            _configuration = CreateDefaultConfiguration();
            await SaveAsync().ConfigureAwait(false);
        }

        return _configuration;
    }

    public async Task SaveAsync()
    {
        try
        {
            var directory = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                try
                {
                    Directory.CreateDirectory(directory);
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new ConfigurationException(
                        $"Cannot create configuration directory '{directory}': Permission denied. " +
                        $"Try running with elevated privileges or choose a different location.", ex);
                }
                catch (IOException ex)
                {
                    throw new ConfigurationException(
                        $"Cannot create configuration directory '{directory}': {ex.Message}", ex);
                }
                catch (NotSupportedException ex)
                {
                    throw new ConfigurationException(
                        $"Cannot create configuration directory '{directory}': Invalid path format", ex);
                }
            }

            // Use buffered async file stream for efficient writes
            await using var stream = new FileStream(_configPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await JsonSerializer.SerializeAsync(stream, _configuration, s_writeOptions).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ConfigurationException(
                $"Cannot save configuration to {_configPath}: Permission denied. " +
                $"Try running with elevated privileges or choose a different location.", ex);
        }
        catch (IOException ex)
        {
            throw new ConfigurationException($"Cannot save configuration to {_configPath}: {ex.Message}", ex);
        }
    }

    public Configuration GetConfiguration() => _configuration;

    public async Task UpdateAsync(Configuration config)
    {
        _configuration = config ?? throw new ArgumentNullException(nameof(config));
        await SaveAsync();
    }

    public List<HandlerConfig> GetHandlersForHook(string hookEventName)
    {
        if (_configuration.HookHandlers.TryGetValue(hookEventName, out var hookConfig))
        {
            return hookConfig.Enabled ? hookConfig.Handlers : new List<HandlerConfig>();
        }
        return new List<HandlerConfig>();
    }

    public HandlerConfig? FindMatchingHandler(string hookEventName, string? toolName, string provider = "claude")
    {
        // 1. Check for handlers with explicit Client matching this provider
        var allHandlers = GetHandlersForHook(hookEventName);
        var clientMatch = allHandlers
            .Where(h => string.Equals(h.Client, provider, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(h => h.Matches(toolName ?? string.Empty));
        if (clientMatch != null)
            return clientMatch;

        // 2. Check deprecated Copilot.HookHandlers section (backwards compat)
        if (string.Equals(provider, "copilot", StringComparison.OrdinalIgnoreCase))
        {
            var copilotHandlers = GetHandlersForHook(hookEventName, provider: "copilot");
            var legacyMatch = copilotHandlers.FirstOrDefault(h => h.Matches(toolName ?? string.Empty));
            if (legacyMatch != null)
                return legacyMatch;
        }

        // 3. Fall back to handlers with Client = null (applies to all clients)
        return allHandlers
            .Where(h => h.Client == null)
            .FirstOrDefault(h => h.Matches(toolName ?? string.Empty));
    }

    public List<HandlerConfig> GetHandlersForHook(string hookEventName, string provider)
    {
        if (string.Equals(provider, "copilot", StringComparison.OrdinalIgnoreCase))
        {
            if (_configuration.Copilot.Enabled &&
                _configuration.Copilot.HookHandlers.TryGetValue(hookEventName, out var copilotConfig))
            {
                return copilotConfig.Enabled ? copilotConfig.Handlers : new List<HandlerConfig>();
            }
        }

        return GetHandlersForHook(hookEventName);
    }

    private static string GetDefaultConfigPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".leash", "config.json");
    }

    private static string GetPromptsDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".leash", "prompts");
    }

    private static Configuration CreateDefaultConfiguration()
    {
        var promptsDir = GetPromptsDir();

        return new Configuration
        {
            Llm = new LlmConfig
            {
                Provider = "claude-persistent",
                Model = "opus",
                Timeout = 30000,
                SystemPrompt = "You are a security analyzer that evaluates the safety of operations. Always respond ONLY with valid JSON containing safetyScore (0-100), reasoning (string), and category (safe|cautious|risky|dangerous). Never include any text outside the JSON object."
            },
            Server = new ServerConfig
            {
                Port = 5050,
                Host = "localhost"
            },
            Session = new SessionConfig
            {
                MaxHistoryPerSession = 50,
                StorageDir = "~/.leash/sessions"
            },
            HookHandlers = new Dictionary<string, HookEventConfig>
            {
                ["PermissionRequest"] = new HookEventConfig
                {
                    Enabled = true,
                    Handlers = new List<HandlerConfig>
                    {
                        new() { Name = "bash-analyzer", Matcher = "Bash", Mode = "llm-analysis",
                            PromptTemplate = Path.Combine(promptsDir, "bash-prompt.txt"),
                            Threshold = 95, AutoApprove = true,
                            Config = new Dictionary<string, object>
                            {
                                ["sendCode"] = false,
                                ["knownSafeCommands"] = new[] { "git status", "git log", "git diff", "git branch",
                                    "npm install", "npm test", "npm run", "dotnet build", "dotnet test", "dotnet restore",
                                    "ls", "pwd", "cat", "head", "tail", "wc", "echo", "date", "find", "which", "whoami" }
                            }
                        },
                        new() { Name = "file-read-analyzer", Matcher = "Read", Mode = "llm-analysis",
                            PromptTemplate = Path.Combine(promptsDir, "file-read-prompt.txt"),
                            Threshold = 93, AutoApprove = true,
                            Config = new Dictionary<string, object> { ["sendCode"] = false, ["allowSendCodeIfConfigured"] = true }
                        },
                        new() { Name = "file-write-analyzer", Matcher = "Write|Edit", Mode = "llm-analysis",
                            PromptTemplate = Path.Combine(promptsDir, "file-write-prompt.txt"),
                            Threshold = 97, AutoApprove = true,
                            Config = new Dictionary<string, object> { ["sendCode"] = false }
                        },
                        new() { Name = "web-analyzer", Matcher = "WebFetch|WebSearch", Mode = "llm-analysis",
                            PromptTemplate = Path.Combine(promptsDir, "web-prompt.txt"),
                            Threshold = 90, AutoApprove = true,
                            Config = new Dictionary<string, object>
                            {
                                ["knownSafeDomains"] = new[] { "github.com", "*.microsoft.com", "npmjs.com", "pypi.org",
                                    "stackoverflow.com", "docs.microsoft.com", "developer.mozilla.org",
                                    "docs.python.org", "crates.io", "pkg.go.dev", "learn.microsoft.com" }
                            }
                        },
                        new() { Name = "mcp-analyzer", Matcher = "mcp__.*", Mode = "llm-analysis",
                            PromptTemplate = Path.Combine(promptsDir, "mcp-prompt.txt"),
                            Threshold = 92, AutoApprove = true,
                            Config = new Dictionary<string, object> { ["autoApproveRegistered"] = true }
                        }
                    }
                },
                ["PreToolUse"] = new HookEventConfig
                {
                    Enabled = true,
                    Handlers = new List<HandlerConfig>
                    {
                        new() { Name = "pre-tool-analyzer", Matcher = "Bash|Write|Edit", Mode = "llm-analysis",
                            PromptTemplate = Path.Combine(promptsDir, "pre-tool-use-prompt.txt"),
                            Threshold = 80, AutoApprove = true },
                        new() { Name = "pre-tool-logger", Matcher = "*", Mode = "log-only", Client = "claude",
                            Config = new Dictionary<string, object> { ["logLevel"] = "info" } },
                        new() { Name = "copilot-pre-tool", Matcher = "*", Mode = "llm-analysis", Client = "copilot",
                            PromptTemplate = Path.Combine(promptsDir, "pre-tool-use-prompt.txt"),
                            Threshold = 85, AutoApprove = true }
                    }
                },
                ["PostToolUse"] = new HookEventConfig
                {
                    Enabled = true,
                    Handlers = new List<HandlerConfig>
                    {
                        new() { Name = "post-tool-validator", Matcher = "Write|Edit", Mode = "llm-analysis",
                            PromptTemplate = Path.Combine(promptsDir, "post-tool-validation-prompt.txt"),
                            Config = new Dictionary<string, object> { ["checkForErrors"] = true } },
                        new() { Name = "post-tool-logger", Matcher = "*", Mode = "log-only" }
                    }
                },
                ["PostToolUseFailure"] = new HookEventConfig
                {
                    Enabled = true,
                    Handlers = new List<HandlerConfig>
                    {
                        new() { Name = "failure-analyzer", Matcher = "*", Mode = "llm-analysis",
                            PromptTemplate = Path.Combine(promptsDir, "failure-analysis-prompt.txt"),
                            Config = new Dictionary<string, object> { ["suggestFixes"] = true } }
                    }
                },
                ["UserPromptSubmit"] = new HookEventConfig
                {
                    Enabled = true,
                    Handlers = new List<HandlerConfig>
                    {
                        new() { Name = "prompt-logger", Mode = "log-only" },
                        new() { Name = "context-injector", Mode = "context-injection",
                            PromptTemplate = Path.Combine(promptsDir, "context-injection-prompt.txt"),
                            Config = new Dictionary<string, object> { ["injectGitBranch"] = true, ["injectRecentErrors"] = true } }
                    }
                },
                ["Stop"] = new HookEventConfig
                {
                    Enabled = true,
                    Handlers = new List<HandlerConfig>
                    {
                        new() { Name = "stop-logger", Mode = "log-only" }
                    }
                }
            },
            Copilot = new CopilotConfig
            {
                Enabled = true,
                HookHandlers = new Dictionary<string, HookEventConfig>
                {
                    ["PreToolUse"] = new HookEventConfig
                    {
                        Enabled = true,
                        Handlers = new List<HandlerConfig>
                        {
                            new() { Name = "copilot-bash-analyzer", Matcher = "bash", Mode = "llm-analysis",
                                PromptTemplate = Path.Combine(promptsDir, "bash-prompt.txt"),
                                Threshold = 95, AutoApprove = true },
                            new() { Name = "copilot-pre-tool-logger", Matcher = "*", Mode = "log-only" }
                        }
                    },
                    ["PostToolUse"] = new HookEventConfig
                    {
                        Enabled = true,
                        Handlers = new List<HandlerConfig>
                        {
                            new() { Name = "copilot-post-tool-logger", Matcher = "*", Mode = "log-only" }
                        }
                    }
                }
            }
        };
    }
}
