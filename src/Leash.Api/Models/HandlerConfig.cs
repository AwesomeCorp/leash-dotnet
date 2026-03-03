using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Leash.Api.Models;

public class HandlerConfig
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static ILogger<HandlerConfig>? _staticLogger;
    private Lazy<Regex?>? _compiledRegex;
    private string? _lastMatcherValue;
    private readonly object _regexLock = new object();

    public string Name { get; set; } = string.Empty;

    public static void SetLogger(ILogger<HandlerConfig> logger)
    {
        if (logger == null)
            throw new ArgumentNullException(nameof(logger));

        // Thread-safe: only set if currently null
        Interlocked.CompareExchange(ref _staticLogger, logger, null);
    }

    private string? _matcher;
    public string? Matcher
    {
        get => _matcher;
        set
        {
            if (_matcher != value)
            {
                _matcher = value;
                // Invalidate cache when matcher changes
                lock (_regexLock)
                {
                    _compiledRegex = null;
                    _lastMatcherValue = null;
                }
            }
        }
    }

    public string Mode { get; set; } = "log-only";
    public string? PromptTemplate { get; set; }
    public string? Client { get; set; }
    public int Threshold { get; set; } = 85;
    public int ThresholdStrict { get; set; } = 95;
    public int ThresholdModerate { get; set; } = 85;
    public int ThresholdPermissive { get; set; } = 70;
    public bool AutoApprove { get; set; } = false;
    public Dictionary<string, object> Config { get; set; } = new();

    /// <summary>
    /// Gets the threshold for the given profile name.
    /// Falls back to the default Threshold if profile is unknown.
    /// </summary>
    public int GetThresholdForProfile(string? profile)
    {
        return profile?.ToLowerInvariant() switch
        {
            "strict" => ThresholdStrict,
            "moderate" => ThresholdModerate,
            "permissive" => ThresholdPermissive,
            "lockdown" => 101, // Nothing passes - lockdown means no auto-approve
            _ => Threshold
        };
    }

    public bool Matches(string toolName)
    {
        if (string.IsNullOrEmpty(Matcher) || Matcher == "*")
            return true;

        // Thread-safe lazy initialization of compiled regex
        Lazy<Regex?>? lazyRegex;
        lock (_regexLock)
        {
            // Check if we need to create new lazy initializer
            if (_compiledRegex == null || _lastMatcherValue != Matcher)
            {
                var currentMatcher = Matcher;
                _lastMatcherValue = currentMatcher;

                _compiledRegex = new Lazy<Regex?>(() =>
                {
                    try
                    {
                        return new Regex(
                            currentMatcher,
                            RegexOptions.IgnoreCase | RegexOptions.Compiled,
                            RegexTimeout);
                    }
                    catch (ArgumentException ex)
                    {
                        // Invalid regex pattern - fall back to literal match
                        _staticLogger?.LogError(ex, "Invalid regex pattern '{Pattern}': {Message}", currentMatcher, ex.Message);
                        return null;
                    }
                }, LazyThreadSafetyMode.ExecutionAndPublication);
            }

            lazyRegex = _compiledRegex;
        }

        // Use cached compiled regex if available
        var regex = lazyRegex.Value;
        if (regex != null)
        {
            try
            {
                return regex.IsMatch(toolName);
            }
            catch (RegexMatchTimeoutException ex)
            {
                _staticLogger?.LogWarning(ex, "Regex match timeout for pattern '{Pattern}' - possible ReDoS attack", Matcher);
                return false;
            }
        }

        // Fall back to literal string comparison
        return Matcher.Equals(toolName, StringComparison.OrdinalIgnoreCase);
    }
}
