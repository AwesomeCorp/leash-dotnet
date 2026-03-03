#if WINDOWS
using System.Runtime.Versioning;
using Leash.Api.Models;

namespace Leash.Api.Services.Tray.Windows;

/// <summary>
/// Small borderless WinForms popup positioned near the system tray area.
/// Shows tool name, safety score, reasoning snippet, and Approve/Deny buttons.
/// Auto-closes on timeout.
/// </summary>
[SupportedOSPlatform("windows")]
public class TrayDecisionForm : System.Windows.Forms.Form
{
    public event EventHandler<TrayDecision?>? DecisionMade;

    private readonly System.Windows.Forms.Timer _timer;
    private int _remainingSeconds;

    public TrayDecisionForm(NotificationInfo info, int timeoutSeconds)
    {
        _remainingSeconds = timeoutSeconds;

        // Form setup — borderless, positioned near system tray
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedToolWindow;
        StartPosition = System.Windows.Forms.FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Size = new System.Drawing.Size(380, 280);
        BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
        ForeColor = System.Drawing.Color.White;

        // Position near system tray (bottom-right of screen)
        var screen = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        Location = new System.Drawing.Point(
            screen.Right - Size.Width - 12,
            screen.Bottom - Size.Height - 12);

        int y = 10;

        // Title label
        var titleLabel = new System.Windows.Forms.Label
        {
            Text = info.Title,
            Font = new System.Drawing.Font("Segoe UI", 10, System.Drawing.FontStyle.Bold),
            ForeColor = info.Level == NotificationLevel.Danger
                ? System.Drawing.Color.FromArgb(239, 68, 68)
                : System.Drawing.Color.FromArgb(251, 191, 36),
            Location = new System.Drawing.Point(12, y),
            Size = new System.Drawing.Size(356, 22),
            AutoEllipsis = true
        };
        Controls.Add(titleLabel);
        y += 24;

        // Caller + Tool + Score + Category
        var providerName = info.Provider == "copilot" ? "Copilot CLI" : "Claude Code";
        var categoryText = info.Category != null ? $"   Category: {info.Category}" : "";
        var detailText = $"Caller: {providerName}   Tool: {info.ToolName ?? "unknown"}   Score: {info.SafetyScore ?? 0}{categoryText}";
        var toolLabel = new System.Windows.Forms.Label
        {
            Text = detailText,
            Font = new System.Drawing.Font("Segoe UI", 8.5f),
            ForeColor = System.Drawing.Color.FromArgb(200, 200, 200),
            Location = new System.Drawing.Point(12, y),
            Size = new System.Drawing.Size(356, 32),
            AutoEllipsis = true
        };
        Controls.Add(toolLabel);
        y += 34;

        // Command preview (if available)
        if (!string.IsNullOrEmpty(info.CommandPreview))
        {
            var cmdText = info.CommandPreview.Length > 120 ? info.CommandPreview[..117] + "..." : info.CommandPreview;
            var cmdLabel = new System.Windows.Forms.Label
            {
                Text = $"Command: {cmdText}",
                Font = new System.Drawing.Font("Consolas", 8),
                ForeColor = System.Drawing.Color.FromArgb(160, 200, 255),
                Location = new System.Drawing.Point(12, y),
                Size = new System.Drawing.Size(356, 30),
                AutoEllipsis = true
            };
            Controls.Add(cmdLabel);
            y += 32;
        }

        // Reasoning snippet
        var reasoning = info.Reasoning ?? "";
        if (reasoning.Length > 150)
            reasoning = reasoning[..147] + "...";
        var reasonLabel = new System.Windows.Forms.Label
        {
            Text = reasoning,
            Font = new System.Drawing.Font("Segoe UI", 8.5f),
            ForeColor = System.Drawing.Color.FromArgb(170, 170, 170),
            Location = new System.Drawing.Point(12, y),
            Size = new System.Drawing.Size(356, 48),
            AutoEllipsis = true
        };
        Controls.Add(reasonLabel);
        y += 50;

        // Countdown label
        var countdownLabel = new System.Windows.Forms.Label
        {
            Text = $"Auto-dismiss in {_remainingSeconds}s",
            Font = new System.Drawing.Font("Segoe UI", 8),
            ForeColor = System.Drawing.Color.FromArgb(130, 130, 130),
            Location = new System.Drawing.Point(12, y),
            Size = new System.Drawing.Size(356, 16)
        };
        Controls.Add(countdownLabel);
        y += 20;

        // Approve button
        var approveBtn = new System.Windows.Forms.Button
        {
            Text = "Approve",
            Font = new System.Drawing.Font("Segoe UI", 9, System.Drawing.FontStyle.Bold),
            BackColor = System.Drawing.Color.FromArgb(34, 197, 94),
            ForeColor = System.Drawing.Color.White,
            FlatStyle = System.Windows.Forms.FlatStyle.Flat,
            Location = new System.Drawing.Point(12, y),
            Size = new System.Drawing.Size(170, 36),
            Cursor = System.Windows.Forms.Cursors.Hand
        };
        approveBtn.FlatAppearance.BorderSize = 0;
        approveBtn.Click += (_, _) =>
        {
            DecisionMade?.Invoke(this, TrayDecision.Approve);
            Close();
        };
        Controls.Add(approveBtn);

        // Deny button
        var denyBtn = new System.Windows.Forms.Button
        {
            Text = "Deny",
            Font = new System.Drawing.Font("Segoe UI", 9, System.Drawing.FontStyle.Bold),
            BackColor = System.Drawing.Color.FromArgb(239, 68, 68),
            ForeColor = System.Drawing.Color.White,
            FlatStyle = System.Windows.Forms.FlatStyle.Flat,
            Location = new System.Drawing.Point(194, y),
            Size = new System.Drawing.Size(170, 36),
            Cursor = System.Windows.Forms.Cursors.Hand
        };
        denyBtn.FlatAppearance.BorderSize = 0;
        denyBtn.Click += (_, _) =>
        {
            DecisionMade?.Invoke(this, TrayDecision.Deny);
            Close();
        };
        Controls.Add(denyBtn);

        // Adjust form height to fit content
        Size = new System.Drawing.Size(380, y + 50);
        denyBtn.FlatAppearance.BorderSize = 0;
        denyBtn.Click += (_, _) =>
        {
            DecisionMade?.Invoke(this, TrayDecision.Deny);
            Close();
        };
        Controls.Add(denyBtn);

        // Timeout timer
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) =>
        {
            _remainingSeconds--;
            countdownLabel.Text = $"Auto-dismiss in {_remainingSeconds}s";
            if (_remainingSeconds <= 0)
            {
                _timer.Stop();
                DecisionMade?.Invoke(this, null);
                Close();
            }
        };
        _timer.Start();
    }

    protected override void OnFormClosed(System.Windows.Forms.FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }
}
#endif
