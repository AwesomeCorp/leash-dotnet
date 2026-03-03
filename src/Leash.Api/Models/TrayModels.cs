namespace Leash.Api.Models;

public enum NotificationLevel
{
    Info,
    Warning,
    Danger
}

public enum TrayDecision
{
    Approve,
    Deny
}

public record NotificationInfo
{
    public required string Title { get; init; }
    public required string Body { get; init; }
    public string? ToolName { get; init; }
    public int? SafetyScore { get; init; }
    public string? Reasoning { get; init; }
    public string? Category { get; init; }
    public string? DecisionId { get; init; }
    public string? Provider { get; init; }
    public string? CommandPreview { get; init; }
    public NotificationLevel Level { get; init; } = NotificationLevel.Info;
}

public class PendingDecision
{
    public required string Id { get; init; }
    public required TaskCompletionSource<TrayDecision?> Tcs { get; init; }
    public required NotificationInfo Info { get; init; }
    public required CancellationTokenSource TimeoutCts { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public class TrayConfig
{
    /// <summary>Master switch for the tray/notification feature. Default: enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Show passive alerts for denied events.</summary>
    public bool AlertOnDenied { get; set; } = true;

    /// <summary>Show passive alerts for uncertain/passthrough events.</summary>
    public bool AlertOnUncertain { get; set; } = true;

    /// <summary>Enable interactive approve/deny dialogs for uncertain events.</summary>
    public bool InteractiveEnabled { get; set; } = true;

    /// <summary>Timeout in seconds for interactive dialogs before falling through. Must be less than 30 (Kestrel timeout).</summary>
    public int InteractiveTimeoutSeconds { get; set; } = 10;

    /// <summary>Minimum safety score to show interactive dialog (below this, auto-deny in enforce mode).</summary>
    public int InteractiveScoreMin { get; set; } = 30;

    /// <summary>Maximum safety score to show interactive dialog (above this, auto-approve).</summary>
    public int InteractiveScoreMax { get; set; } = 85;
}
