using Leash.Api.Services;
using Leash.Api.Services.Tray;
using Leash.Api.Models;
using Leash.Api.Middleware;
using Microsoft.AspNetCore.ResponseCompression;
using System.Diagnostics;

// ---- Parse CLI arguments ----
var installHooks = args.Contains("--install-hooks");
var installCopilotHooks = args.Contains("--install-copilot-hooks");
var noHooks = args.Contains("--no-hooks");
var enforceMode = args.Contains("--enforce");
var copilotRepoArg = args.FirstOrDefault(a => a.StartsWith("--copilot-repo="));
var copilotRepoPath = copilotRepoArg?.Split('=', 2).ElementAtOrDefault(1);

// Filter our custom args out before passing to WebApplication
var webArgs = args.Where(a =>
    a != "--install-hooks" && a != "--install-copilot-hooks" &&
    a != "--no-hooks" && a != "--enforce" &&
    !a.StartsWith("--copilot-repo=")).ToArray();

var builder = WebApplication.CreateBuilder(webArgs);

// Add services
builder.Services.AddControllers(options =>
{
    // Limit request body size to 1MB to prevent DoS attacks
    options.MaxModelBindingCollectionSize = 1000;
});

// Add memory cache for session management with size limit
builder.Services.AddMemoryCache(options =>
{
    options.SizeLimit = 1000;
});

// Enable response compression
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        new[] { "application/json", "text/event-stream" });
});

builder.Services.Configure<GzipCompressionProviderOptions>(options =>
{
    options.Level = System.IO.Compression.CompressionLevel.Fastest;
});

// Configure Kestrel server limits
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 1 * 1024 * 1024; // 1MB (reduced from 10MB)
    options.Limits.MaxRequestLineSize = 8192;
    options.Limits.MaxRequestHeadersTotalSize = 32768;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
});

// Configure application services
var configPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".leash",
    "config.json");

// Load configuration without logger (logger will be injected by DI after build)
Configuration config;
try
{
    var configManager = new Leash.Api.Services.ConfigurationManager(configPath);
    config = await configManager.LoadAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FATAL: Failed to initialize application: {ex.Message}");
    Console.Error.WriteLine("Check configuration file permissions and format.");
    Console.Error.WriteLine($"Config path: {configPath}");
    Environment.Exit(1);
    return; // Unreachable, but satisfies compiler
}

// Apply --enforce flag to config
if (enforceMode)
{
    config.EnforcementEnabled = true;
    config.EnforcementMode = "enforce";
}

// Validate server host - must be localhost/loopback only
var allowedHosts = new[] { "localhost", "127.0.0.1", "::1" };
if (!allowedHosts.Contains(config.Server.Host, StringComparer.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"FATAL: Server host '{config.Server.Host}' is not allowed. Only localhost/loopback addresses are permitted.");
    Console.Error.WriteLine("This service is designed to run locally only.");
    Environment.Exit(1);
    return;
}

// Determine prompts directory
var promptsDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".leash",
    "prompts");

// Seed default prompt templates if the prompts directory is empty
if (!Directory.Exists(promptsDir))
    Directory.CreateDirectory(promptsDir);
if (Directory.GetFiles(promptsDir, "*.txt").Length == 0)
{
    var defaultPromptsDir = Path.Combine(AppContext.BaseDirectory, "prompts");
    if (!Directory.Exists(defaultPromptsDir))
        defaultPromptsDir = Path.Combine(Directory.GetCurrentDirectory(), "prompts");
    if (Directory.Exists(defaultPromptsDir))
    {
        foreach (var file in Directory.GetFiles(defaultPromptsDir, "*.txt"))
        {
            var dest = Path.Combine(promptsDir, Path.GetFileName(file));
            if (!File.Exists(dest))
                File.Copy(file, dest);
        }
    }
}

// Register services with DI container
builder.Services.AddSingleton(sp =>
{
    // Create ConfigurationManager with proper logger from DI
    var logger = sp.GetRequiredService<ILogger<Leash.Api.Services.ConfigurationManager>>();
    return new Leash.Api.Services.ConfigurationManager(configPath, logger);
});

builder.Services.AddSingleton(sp =>
    new SessionManager(
        config.Session.StorageDir,
        config.Session.MaxHistoryPerSession,
        sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
        sp.GetRequiredService<ILogger<SessionManager>>()));

builder.Services.AddSingleton<TerminalOutputService>();

// Register HttpClient for LLM API clients (Anthropic, Generic REST)
builder.Services.AddHttpClient("LLMClient")
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(5));

builder.Services.AddSingleton<LLMClientProvider>(sp =>
    new LLMClientProvider(
        sp.GetRequiredService<Leash.Api.Services.ConfigurationManager>(),
        sp.GetRequiredService<ILoggerFactory>(),
        sp.GetRequiredService<ILogger<LLMClientProvider>>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<TerminalOutputService>()));

// Forward ILLMClient to LLMClientProvider for backward compatibility
builder.Services.AddSingleton<ILLMClient>(sp => sp.GetRequiredService<LLMClientProvider>());

builder.Services.AddSingleton(sp =>
    new PromptTemplateService(promptsDir, sp.GetRequiredService<ILogger<PromptTemplateService>>()));

builder.Services.AddSingleton(sp =>
    new TranscriptWatcher(sp.GetRequiredService<ILogger<TranscriptWatcher>>()));

builder.Services.AddSingleton<HookHandlerFactory>(sp =>
    new HookHandlerFactory(
        sp,
        sp.GetRequiredService<LLMClientProvider>(),
        sp.GetRequiredService<PromptTemplateService>(),
        sp.GetRequiredService<SessionManager>(),
        sp.GetRequiredService<ILogger<HookHandlerFactory>>()));

builder.Services.AddSingleton<ProfileService>(sp =>
    new ProfileService(
        sp.GetRequiredService<Leash.Api.Services.ConfigurationManager>(),
        sp.GetRequiredService<ILogger<ProfileService>>()));

builder.Services.AddSingleton<AdaptiveThresholdService>(sp =>
    new AdaptiveThresholdService(
        config.Session.StorageDir.Replace("~/", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/"),
        sp.GetRequiredService<ILogger<AdaptiveThresholdService>>()));

builder.Services.AddSingleton<InsightsEngine>(sp =>
    new InsightsEngine(
        sp.GetRequiredService<AdaptiveThresholdService>(),
        sp.GetRequiredService<SessionManager>(),
        sp.GetRequiredService<ILogger<InsightsEngine>>()));

builder.Services.AddSingleton<AuditReportGenerator>(sp =>
    new AuditReportGenerator(
        sp.GetRequiredService<SessionManager>(),
        sp.GetRequiredService<AdaptiveThresholdService>(),
        sp.GetRequiredService<ProfileService>()));

builder.Services.AddSingleton<EnforcementService>(sp =>
    new EnforcementService(
        sp.GetRequiredService<Leash.Api.Services.ConfigurationManager>(),
        sp.GetRequiredService<ILogger<EnforcementService>>()));

builder.Services.AddSingleton<HookInstaller>(sp =>
    new HookInstaller(
        sp.GetRequiredService<ILogger<HookInstaller>>(),
        sp.GetRequiredService<Leash.Api.Services.ConfigurationManager>(),
        $"http://{config.Server.Host}:{config.Server.Port}"));

builder.Services.AddSingleton<CopilotHookInstaller>(sp =>
    new CopilotHookInstaller(
        sp.GetRequiredService<ILogger<CopilotHookInstaller>>(),
        $"http://{config.Server.Host}:{config.Server.Port}"));

// Console status line for aggregated stats
var appUrl = $"http://{config.Server.Host}:{config.Server.Port}";
builder.Services.AddSingleton<ConsoleStatusService>();

// Event trigger service for webhooks
builder.Services.AddSingleton<TriggerService>();

// ---- System tray & native notifications (disabled by default) ----
builder.Services.AddSingleton<PendingDecisionService>();

// Platform-detected factory for ITrayService (always register real service; enabled check is at call site)
builder.Services.AddSingleton<ITrayService>(sp =>
{
#if WINDOWS
    return new Leash.Api.Services.Tray.Windows.WindowsTrayService(
        sp.GetRequiredService<ILogger<Leash.Api.Services.Tray.Windows.WindowsTrayService>>(),
        appUrl);
#else
    if (OperatingSystem.IsMacOS())
        return new Leash.Api.Services.Tray.Mac.MacTrayService(
            sp.GetRequiredService<ILogger<Leash.Api.Services.Tray.Mac.MacTrayService>>());
    if (OperatingSystem.IsLinux())
        return new Leash.Api.Services.Tray.Linux.LinuxTrayService(
            sp.GetRequiredService<ILogger<Leash.Api.Services.Tray.Linux.LinuxTrayService>>());
    return new NullTrayService();
#endif
});

// Platform-detected factory for INotificationService (always register real service)
builder.Services.AddSingleton<INotificationService>(sp =>
{
#if WINDOWS
    var winTray = sp.GetRequiredService<ITrayService>() as Leash.Api.Services.Tray.Windows.WindowsTrayService;
    if (winTray != null)
        return new Leash.Api.Services.Tray.Windows.WindowsNotificationService(
            winTray,
            sp.GetRequiredService<PendingDecisionService>(),
            sp.GetRequiredService<ILogger<Leash.Api.Services.Tray.Windows.WindowsNotificationService>>());
    return new NullNotificationService();
#else
    if (OperatingSystem.IsMacOS())
        return new Leash.Api.Services.Tray.Mac.MacNotificationService(
            sp.GetRequiredService<ILogger<Leash.Api.Services.Tray.Mac.MacNotificationService>>());
    if (OperatingSystem.IsLinux())
        return new Leash.Api.Services.Tray.Linux.LinuxNotificationService(
            sp.GetRequiredService<ILogger<Leash.Api.Services.Tray.Linux.LinuxNotificationService>>());
    return new NullNotificationService();
#endif
});

// Add OpenAPI/Swagger documentation
builder.Services.AddOpenApi();

// Add CORS - restrict to localhost only for security
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
                $"http://localhost:{config.Server.Port}",
                $"http://127.0.0.1:{config.Server.Port}",
                $"https://localhost:{config.Server.Port}",
                $"https://127.0.0.1:{config.Server.Port}")
              .WithMethods("GET", "POST", "PUT", "DELETE") // Only allow needed methods
              .WithHeaders("Content-Type", "X-Api-Key"); // Only allow needed headers
    });
});

var app = builder.Build();

// Set static logger for HandlerConfig
var handlerConfigLogger = app.Services.GetRequiredService<ILogger<HandlerConfig>>();
HandlerConfig.SetLogger(handlerConfigLogger);

// Start TranscriptWatcher
var transcriptWatcher = app.Services.GetRequiredService<TranscriptWatcher>();
transcriptWatcher.Start();

// Initialize ProfileService
var profileService = app.Services.GetRequiredService<ProfileService>();
await profileService.InitializeAsync();

// Initialize AdaptiveThresholdService
var adaptiveService = app.Services.GetRequiredService<AdaptiveThresholdService>();
await adaptiveService.LoadAsync();

// Start tray service if enabled
var trayService = app.Services.GetRequiredService<ITrayService>();
if (config.Tray.Enabled)
{
    try { await trayService.StartAsync(); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Warning: Tray service failed to start: {ex.Message}");
    }
}

// ---- Hook installation ----
var hookInstaller = app.Services.GetRequiredService<HookInstaller>();
var copilotHookInstaller = app.Services.GetRequiredService<CopilotHookInstaller>();
bool hooksInstalledThisSession = false;
bool copilotHooksInstalledThisSession = false;

// Always install Claude hooks unless --no-hooks is specified.
// Users can manage hooks from the dashboard. Hooks are always removed on shutdown.
if (!noHooks)
{
    hookInstaller.Install();
    hooksInstalledThisSession = true;
}

// Copilot hooks only via explicit CLI flag
if (installCopilotHooks)
{
    if (!string.IsNullOrEmpty(copilotRepoPath))
        copilotHookInstaller.InstallRepo(copilotRepoPath);
    else
        copilotHookInstaller.InstallCwd();
    copilotHooksInstalledThisSession = true;
}

// Apply enforcement mode from CLI
if (enforceMode)
{
    var enforcementService = app.Services.GetRequiredService<EnforcementService>();
    await enforcementService.SetModeAsync("enforce");
}

// Ensure services are disposed on shutdown
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var sessionManager = app.Services.GetRequiredService<SessionManager>();

// Auto-cleanup hooks on shutdown if we installed them this session
lifetime.ApplicationStopping.Register(() =>
{
    if (hooksInstalledThisSession)
    {
        try
        {
            hookInstaller.Uninstall();
            Console.WriteLine("Claude hooks removed on shutdown.");
        }
        catch (Exception ex)
        {
            var logger = app.Services.GetRequiredService<ILogger<Program>>();
            logger.LogError(ex, "Failed to uninstall Claude hooks during shutdown");
        }
    }

    if (copilotHooksInstalledThisSession)
    {
        try
        {
            if (!string.IsNullOrEmpty(copilotRepoPath))
                copilotHookInstaller.UninstallRepo(copilotRepoPath);
            else
                copilotHookInstaller.UninstallCwd();
            Console.WriteLine("Copilot hooks removed on shutdown.");
        }
        catch (Exception ex)
        {
            var logger = app.Services.GetRequiredService<ILogger<Program>>();
            logger.LogError(ex, "Failed to uninstall Copilot hooks during shutdown");
        }
    }

    try
    {
        trayService.Dispose();
    }
    catch (Exception ex)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Failed to dispose tray service during shutdown");
    }

    try
    {
        transcriptWatcher.Dispose();
    }
    catch (Exception ex)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Failed to dispose TranscriptWatcher during shutdown");
    }

    try
    {
        sessionManager.Dispose();
    }
    catch (Exception ex)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Failed to dispose SessionManager during shutdown");
    }

    try
    {
        var llmClient = app.Services.GetRequiredService<ILLMClient>();
        if (llmClient is IDisposable disposableLlm)
            disposableLlm.Dispose();
    }
    catch (Exception ex)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Failed to dispose LLM client during shutdown");
    }
});

// Auto-launch browser after the server starts
lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        Process.Start(new ProcessStartInfo(appUrl) { UseShellExecute = true });
    }
    catch
    {
        // Non-fatal - URL already printed in startup banner
    }
});

// Configure middleware pipeline (ORDER MATTERS)

// 1. Security headers on ALL responses
app.UseMiddleware<SecurityHeadersMiddleware>();

// 2. Rate limiting before any processing
app.UseMiddleware<RateLimitingMiddleware>();

// 3. API key authentication
app.UseMiddleware<ApiKeyAuthMiddleware>();

// 4. Response compression for performance
app.UseResponseCompression();

// 5. Global exception handler - prevent error info leakage in production
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred" });
        });
    });
}

// 6. OpenAPI/Swagger documentation endpoint
app.MapOpenApi();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();
app.UseAuthorization();
app.MapControllers();

// Resolve mode label for banner
var resolvedMode = !string.IsNullOrEmpty(config.EnforcementMode) ? config.EnforcementMode : (config.EnforcementEnabled ? "enforce" : "observe");
var modeLabel = resolvedMode.ToUpperInvariant();
var trayLabel = config.Tray.Enabled ? (trayService.IsAvailable ? "active" : "enabled (no tray)") : "disabled";
Console.WriteLine($"  Leash | {modeLabel} mode | {appUrl}");
Console.WriteLine($"  Hooks: {(hooksInstalledThisSession ? "installed" : "skipped")} | Tray: {trayLabel} | Dashboard: {appUrl}");
Console.WriteLine($"  Press Ctrl+C to stop (hooks will be auto-removed)");
Console.WriteLine();

app.Run(appUrl);
