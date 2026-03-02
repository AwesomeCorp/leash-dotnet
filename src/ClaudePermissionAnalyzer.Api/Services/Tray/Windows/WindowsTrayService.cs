#if WINDOWS
using System.Runtime.Versioning;

namespace ClaudePermissionAnalyzer.Api.Services.Tray.Windows;

/// <summary>
/// Windows system tray icon using NotifyIcon on a dedicated STA thread.
/// Supports lazy start — can be constructed without starting, then started later.
/// Uses a hidden helper form for reliable cross-thread marshaling.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsTrayService : ITrayService
{
    private readonly ILogger<WindowsTrayService> _logger;
    private readonly string _dashboardUrl;
    private Thread? _staThread;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Windows.Forms.Form? _marshalForm; // hidden form for reliable Invoke()
    private System.Windows.Forms.ApplicationContext? _appContext;
    private volatile bool _started;
    private volatile bool _disposed;

    public WindowsTrayService(ILogger<WindowsTrayService> logger, string dashboardUrl)
    {
        _logger = logger;
        _dashboardUrl = dashboardUrl;
    }

    public bool IsAvailable => _started && !_disposed && _notifyIcon != null;

    public Task StartAsync()
    {
        if (_started || _disposed) return Task.CompletedTask;

        var tcs = new TaskCompletionSource();

        _staThread = new Thread(() =>
        {
            try
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);

                // Hidden form for reliable cross-thread marshaling
                _marshalForm = new System.Windows.Forms.Form
                {
                    ShowInTaskbar = false,
                    FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                    Size = new System.Drawing.Size(0, 0),
                    Opacity = 0
                };
                _marshalForm.Show();
                _marshalForm.Hide();

                _notifyIcon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = CreateDefaultIcon(),
                    Text = "Claude Observer",
                    Visible = true
                };

                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("Open Dashboard", null, (_, _) =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_dashboardUrl) { UseShellExecute = true });
                    }
                    catch { /* non-fatal */ }
                });
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add("Exit", null, (_, _) =>
                {
                    _appContext?.ExitThread();
                });

                _notifyIcon.ContextMenuStrip = menu;
                _notifyIcon.DoubleClick += (_, _) =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_dashboardUrl) { UseShellExecute = true });
                    }
                    catch { /* non-fatal */ }
                };

                _appContext = new System.Windows.Forms.ApplicationContext();
                _started = true;
                tcs.TrySetResult();
                System.Windows.Forms.Application.Run(_appContext);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Windows tray service failed");
                tcs.TrySetResult(); // Don't block startup on tray failure
            }
        });

        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.IsBackground = true;
        _staThread.Name = "TrayIconThread";
        _staThread.Start();

        return tcs.Task;
    }

    public void UpdateStatus(string status)
    {
        if (!IsAvailable) return;
        try
        {
            InvokeOnStaThread(() => _notifyIcon!.Text = $"Claude Observer - {Truncate(status, 60)}");
        }
        catch { /* non-fatal */ }
    }

    /// <summary>Shows a balloon tip notification near the system tray.</summary>
    public void ShowBalloonTip(string title, string text, System.Windows.Forms.ToolTipIcon icon, int timeoutMs = 5000)
    {
        if (!IsAvailable) return;
        try
        {
            InvokeOnStaThread(() => _notifyIcon!.ShowBalloonTip(timeoutMs, Truncate(title, 63), Truncate(text, 255), icon));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to show balloon tip");
        }
    }

    /// <summary>Marshals an action to the STA thread via the hidden helper form.</summary>
    internal void InvokeOnStaThread(Action action)
    {
        if (!_started || _disposed) return;

        // If already on the STA thread, run directly
        if (Thread.CurrentThread == _staThread)
        {
            action();
            return;
        }

        // Marshal via the hidden form (always has a valid handle)
        if (_marshalForm != null && _marshalForm.IsHandleCreated)
        {
            _marshalForm.Invoke(action);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_notifyIcon != null && _started)
            {
                InvokeOnStaThread(() =>
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _marshalForm?.Close();
                    _marshalForm?.Dispose();
                });
            }
            _appContext?.ExitThread();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing tray service");
        }
    }

    private static System.Drawing.Icon CreateDefaultIcon()
    {
        using var bmp = new System.Drawing.Bitmap(16, 16);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.Color.Transparent);
        using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(59, 130, 246));
        g.FillEllipse(brush, 1, 1, 14, 14);
        using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 1.5f);
        g.DrawLines(pen, new[] {
            new System.Drawing.Point(4, 8),
            new System.Drawing.Point(7, 11),
            new System.Drawing.Point(12, 5)
        });
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private static string Truncate(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..(maxLen - 3)] + "...";
}
#endif
