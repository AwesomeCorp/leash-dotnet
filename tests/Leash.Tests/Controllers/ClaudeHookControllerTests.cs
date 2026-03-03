using Leash.Api.Controllers;
using Leash.Api.Handlers;
using Leash.Api.Models;
using Leash.Api.Services;
using Leash.Api.Services.Harness;
using Leash.Api.Services.Tray;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Net.Http;
using System.Text.Json;
using Xunit;

namespace Leash.Tests.Controllers;

public class ClaudeHookControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SessionManager _sessionManager;
    private readonly Mock<HookHandlerFactory> _mockHandlerFactory;
    private readonly Mock<ProfileService> _mockProfileService;
    private readonly Mock<ILogger<ClaudeHookController>> _mockLogger;

    // Configuration with enforcement OFF by default; tests that need enforcement ON
    // create their own controller via CreateController(enforcementEnabled: true).
    private readonly Leash.Api.Services.ConfigurationManager _configManager;
    private readonly EnforcementService _enforcementService;
    private readonly AdaptiveThresholdService _adaptiveService;
    private readonly HarnessClientRegistry _clientRegistry;
    private readonly ClaudeHookController _controller;

    public ClaudeHookControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "claude-hook-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var sessionsDir = Path.Combine(_tempDir, "sessions");
        _sessionManager = new SessionManager(sessionsDir);
        _adaptiveService = new AdaptiveThresholdService(_tempDir);

        _clientRegistry = new HarnessClientRegistry(new IHarnessClient[]
        {
            new ClaudeHarnessClient(),
            new CopilotHarnessClient()
        });

        // Default configuration: enforcement OFF
        var configuration = new Configuration { EnforcementEnabled = false };
        _configManager = new Leash.Api.Services.ConfigurationManager(
            configuration, NullLogger<Leash.Api.Services.ConfigurationManager>.Instance);

        _enforcementService = new EnforcementService(
            _configManager, NullLogger<EnforcementService>.Instance);

        // Mock HookHandlerFactory - needs 5 constructor parameters, so we use a loose mock.
        // PromptTemplateService requires (string, ILogger) and cannot be Mock.Of<>(),
        // so we create a real instance pointing at the temp prompts directory.
        var promptsDir = Path.Combine(_tempDir, "prompts");
        var promptTemplateService = new PromptTemplateService(promptsDir, NullLogger<PromptTemplateService>.Instance);

        var llmClientProvider = new LLMClientProvider(
            _configManager,
            NullLoggerFactory.Instance,
            NullLogger<LLMClientProvider>.Instance);

        _mockHandlerFactory = new Mock<HookHandlerFactory>(
            MockBehavior.Loose,
            Mock.Of<IServiceProvider>(),
            llmClientProvider,
            promptTemplateService,
            _sessionManager,
            NullLogger<HookHandlerFactory>.Instance);

        // Mock ProfileService - constructor takes ConfigurationManager + optional logger
        _mockProfileService = new Mock<ProfileService>(
            MockBehavior.Loose,
            _configManager,
            NullLogger<ProfileService>.Instance);

        _mockLogger = new Mock<ILogger<ClaudeHookController>>();

        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        var triggerService = new TriggerService(
            _configManager, mockHttpClientFactory.Object, NullLogger<TriggerService>.Instance);

        _controller = new ClaudeHookController(
            _clientRegistry,
            _configManager,
            _sessionManager,
            _mockHandlerFactory.Object,
            _mockProfileService.Object,
            _adaptiveService,
            _enforcementService,
            triggerService,
            new ConsoleStatusService(_enforcementService),
            new NullTrayService(),
            new NullNotificationService(),
            new PendingDecisionService(NullLogger<PendingDecisionService>.Instance),
            _mockLogger.Object);
    }

    public void Dispose()
    {
        _sessionManager.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* cleanup best-effort */ }
    }

    /// <summary>
    /// Helper to create a controller with enforcement explicitly ON or OFF.
    /// Returns a new controller instance so the original _controller is not mutated.
    /// </summary>
    private ClaudeHookController CreateControllerWithEnforcement(
        bool enforcementEnabled,
        Configuration? customConfig = null)
    {
        var config = customConfig ?? new Configuration { EnforcementEnabled = enforcementEnabled };
        config.EnforcementEnabled = enforcementEnabled;

        var configMgr = new Leash.Api.Services.ConfigurationManager(
            config, NullLogger<Leash.Api.Services.ConfigurationManager>.Instance);

        var enforcementSvc = new EnforcementService(
            configMgr, NullLogger<EnforcementService>.Instance);

        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        var triggerSvc = new TriggerService(
            configMgr, mockHttpClientFactory.Object, NullLogger<TriggerService>.Instance);

        return new ClaudeHookController(
            _clientRegistry,
            configMgr,
            _sessionManager,
            _mockHandlerFactory.Object,
            _mockProfileService.Object,
            _adaptiveService,
            enforcementSvc,
            triggerSvc,
            new ConsoleStatusService(enforcementSvc),
            new NullTrayService(),
            new NullNotificationService(),
            new PendingDecisionService(NullLogger<PendingDecisionService>.Instance),
            _mockLogger.Object);
    }

    /// <summary>
    /// Helper to parse a JSON string into a JsonElement.
    /// </summary>
    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Helper to extract the JSON string from a ContentResult.
    /// </summary>
    private static string GetContentString(IActionResult result)
    {
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/json", contentResult.ContentType);
        return contentResult.Content!;
    }

    // -----------------------------------------------------------------------
    // Test 1: Enforcement OFF => always returns empty JSON (passthrough)
    // -----------------------------------------------------------------------
    [Fact]
    public async Task HandleClaudeHook_ReturnsEmptyJson_WhenEnforcementIsOff()
    {
        // Arrange - _controller already has enforcement OFF
        var rawInput = ParseJson(@"{
            ""sessionId"": ""test-session-abc123"",
            ""toolName"": ""Bash"",
            ""toolInput"": { ""command"": ""ls -la"" }
        }");

        // Act
        var result = await _controller.HandleClaudeHook("PermissionRequest", rawInput);

        // Assert
        var json = GetContentString(result);
        Assert.Equal("{}", json);
    }

    // -----------------------------------------------------------------------
    // Test 2: Missing event param => returns BadRequest
    // -----------------------------------------------------------------------
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleClaudeHook_ReturnsBadRequest_ForMissingOrEmptyEvent(string? eventParam)
    {
        // Arrange
        var rawInput = ParseJson(@"{ ""sessionId"": ""test-session-abc123"" }");

        // Act
        var result = await _controller.HandleClaudeHook(eventParam!, rawInput);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
    }

    // -----------------------------------------------------------------------
    // Test 3: Missing sessionId => returns empty JSON
    // -----------------------------------------------------------------------
    [Fact]
    public async Task HandleClaudeHook_ReturnsEmptyJson_ForMissingSessionId()
    {
        // Arrange - enforcement ON so we actually enter the main logic path
        var controller = CreateControllerWithEnforcement(enforcementEnabled: true);
        var rawInput = ParseJson(@"{ ""toolName"": ""Bash"" }");

        // Act
        var result = await controller.HandleClaudeHook("PermissionRequest", rawInput);

        // Assert
        var json = GetContentString(result);
        Assert.Equal("{}", json);
    }

    // -----------------------------------------------------------------------
    // Test 4: No matching handler found => returns empty JSON
    // -----------------------------------------------------------------------
    [Fact]
    public async Task HandleClaudeHook_ReturnsEmptyJson_WhenNoMatchingHandlerFound()
    {
        // Arrange - enforcement ON but empty handler config (no handlers match)
        var config = new Configuration
        {
            EnforcementEnabled = true,
            HookHandlers = new Dictionary<string, HookEventConfig>() // empty => no handler matches
        };
        var controller = CreateControllerWithEnforcement(enforcementEnabled: true, customConfig: config);

        var rawInput = ParseJson(@"{
            ""sessionId"": ""test-session-abc123"",
            ""toolName"": ""Bash""
        }");

        // Act
        var result = await controller.HandleClaudeHook("PermissionRequest", rawInput);

        // Assert
        var json = GetContentString(result);
        Assert.Equal("{}", json);
    }

    // -----------------------------------------------------------------------
    // Test 5: PermissionRequest allow response (Claude-formatted)
    // -----------------------------------------------------------------------
    [Fact]
    public async Task HandleClaudeHook_ReturnsAllowResponse_WhenHandlerApproves()
    {
        // Arrange
        var handlerConfig = new HandlerConfig
        {
            Name = "test-handler",
            Matcher = "Bash",
            Mode = "llm-analysis",
            Threshold = 90,
            AutoApprove = true
        };

        var config = new Configuration
        {
            EnforcementEnabled = true,
            HookHandlers = new Dictionary<string, HookEventConfig>
            {
                ["PermissionRequest"] = new HookEventConfig
                {
                    Enabled = true,
                    Handlers = new List<HandlerConfig> { handlerConfig }
                }
            }
        };

        var controller = CreateControllerWithEnforcement(enforcementEnabled: true, customConfig: config);

        // Mock the handler to return an approved output
        var approvedOutput = new HookOutput
        {
            AutoApprove = true,
            SafetyScore = 96,
            Reasoning = "Safe read-only command",
            Category = "safe",
            Threshold = 90
        };

        var mockHandler = new Mock<IHookHandler>();
        mockHandler
            .Setup(h => h.HandleAsync(
                It.IsAny<HookInput>(),
                It.IsAny<HandlerConfig>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(approvedOutput);

        _mockHandlerFactory
            .Setup(f => f.Create("llm-analysis", It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(mockHandler.Object);

        // ProfileService should return reasonable values
        _mockProfileService
            .Setup(p => p.GetThresholdForTool(It.IsAny<string?>()))
            .Returns(90);
        _mockProfileService
            .Setup(p => p.IsAutoApproveEnabled())
            .Returns(true);

        var rawInput = ParseJson(@"{
            ""sessionId"": ""test-session-abc123"",
            ""toolName"": ""Bash"",
            ""toolInput"": { ""command"": ""git status"" }
        }");

        // Act
        var result = await controller.HandleClaudeHook("PermissionRequest", rawInput);

        // Assert
        var json = GetContentString(result);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("hookSpecificOutput", out var hookOutput));
        Assert.True(hookOutput.TryGetProperty("decision", out var decision));
        Assert.Equal("allow", decision.GetProperty("behavior").GetString());
    }

    // -----------------------------------------------------------------------
    // Test 6: PermissionRequest deny response (Claude-formatted)
    // -----------------------------------------------------------------------
    [Fact]
    public async Task HandleClaudeHook_ReturnsDenyResponse_WhenHandlerDenies()
    {
        // Arrange
        var handlerConfig = new HandlerConfig
        {
            Name = "test-handler",
            Matcher = "Bash",
            Mode = "llm-analysis",
            Threshold = 95,
            AutoApprove = true
        };

        var config = new Configuration
        {
            EnforcementEnabled = true,
            HookHandlers = new Dictionary<string, HookEventConfig>
            {
                ["PermissionRequest"] = new HookEventConfig
                {
                    Enabled = true,
                    Handlers = new List<HandlerConfig> { handlerConfig }
                }
            }
        };

        var controller = CreateControllerWithEnforcement(enforcementEnabled: true, customConfig: config);

        // Mock the handler to return a denied output
        var deniedOutput = new HookOutput
        {
            AutoApprove = false,
            SafetyScore = 40,
            Reasoning = "Potentially destructive command: rm -rf detected",
            Category = "dangerous",
            Threshold = 95,
            Interrupt = false
        };

        var mockHandler = new Mock<IHookHandler>();
        mockHandler
            .Setup(h => h.HandleAsync(
                It.IsAny<HookInput>(),
                It.IsAny<HandlerConfig>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(deniedOutput);

        _mockHandlerFactory
            .Setup(f => f.Create("llm-analysis", It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(mockHandler.Object);

        _mockProfileService
            .Setup(p => p.GetThresholdForTool(It.IsAny<string?>()))
            .Returns(95);
        _mockProfileService
            .Setup(p => p.IsAutoApproveEnabled())
            .Returns(true);

        var rawInput = ParseJson(@"{
            ""sessionId"": ""test-session-abc123"",
            ""toolName"": ""Bash"",
            ""toolInput"": { ""command"": ""rm -rf /"" }
        }");

        // Act
        var result = await controller.HandleClaudeHook("PermissionRequest", rawInput);

        // Assert
        var json = GetContentString(result);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("hookSpecificOutput", out var hookOutput));
        Assert.True(hookOutput.TryGetProperty("decision", out var decision));
        Assert.Equal("deny", decision.GetProperty("behavior").GetString());

        // Verify the deny message contains safety score and reasoning
        var message = decision.GetProperty("message").GetString();
        Assert.NotNull(message);
        Assert.Contains("40", message); // safety score
        Assert.Contains("95", message); // threshold
        Assert.Contains("Potentially destructive command", message);
    }

    // -----------------------------------------------------------------------
    // Test 7: Handler exception => returns empty JSON (error safety)
    // -----------------------------------------------------------------------
    [Fact]
    public async Task HandleClaudeHook_ReturnsEmptyJson_OnHandlerException()
    {
        // Arrange
        var handlerConfig = new HandlerConfig
        {
            Name = "test-handler",
            Matcher = "Bash",
            Mode = "llm-analysis",
            Threshold = 90,
            AutoApprove = true
        };

        var config = new Configuration
        {
            EnforcementEnabled = true,
            HookHandlers = new Dictionary<string, HookEventConfig>
            {
                ["PermissionRequest"] = new HookEventConfig
                {
                    Enabled = true,
                    Handlers = new List<HandlerConfig> { handlerConfig }
                }
            }
        };

        var controller = CreateControllerWithEnforcement(enforcementEnabled: true, customConfig: config);

        // Mock the handler to throw an exception
        var mockHandler = new Mock<IHookHandler>();
        mockHandler
            .Setup(h => h.HandleAsync(
                It.IsAny<HookInput>(),
                It.IsAny<HandlerConfig>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("LLM service unavailable"));

        _mockHandlerFactory
            .Setup(f => f.Create("llm-analysis", It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(mockHandler.Object);

        _mockProfileService
            .Setup(p => p.GetThresholdForTool(It.IsAny<string?>()))
            .Returns(90);
        _mockProfileService
            .Setup(p => p.IsAutoApproveEnabled())
            .Returns(true);

        var rawInput = ParseJson(@"{
            ""sessionId"": ""test-session-abc123"",
            ""toolName"": ""Bash"",
            ""toolInput"": { ""command"": ""ls"" }
        }");

        // Act
        var result = await controller.HandleClaudeHook("PermissionRequest", rawInput);

        // Assert - should return {} not a 500 error
        var json = GetContentString(result);
        Assert.Equal("{}", json);
    }

    // -----------------------------------------------------------------------
    // Additional: Enforcement OFF still returns empty JSON even for valid input
    // -----------------------------------------------------------------------
    [Fact]
    public async Task HandleClaudeHook_ReturnsEmptyJson_WhenEnforcementOff_RegardlessOfInput()
    {
        // Arrange - enforcement OFF, but provide full valid input
        // The controller should always return {} when enforcement is off
        var rawInput = ParseJson(@"{
            ""sessionId"": ""test-session-abc123"",
            ""toolName"": ""Bash"",
            ""toolInput"": { ""command"": ""rm -rf /"" },
            ""cwd"": ""/home/user""
        }");

        // Act
        var result = await _controller.HandleClaudeHook("PermissionRequest", rawInput);

        // Assert
        var json = GetContentString(result);
        Assert.Equal("{}", json);

        // Verify that the handler factory was never called when enforcement is off
        _mockHandlerFactory.Verify(
            f => f.Create(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
    }

    // -----------------------------------------------------------------------
    // Additional: Log-only handler => returns empty JSON
    // -----------------------------------------------------------------------
    [Fact]
    public async Task HandleClaudeHook_ReturnsEmptyJson_WhenHandlerIsLogOnly()
    {
        // Arrange
        var handlerConfig = new HandlerConfig
        {
            Name = "log-only-handler",
            Matcher = "Bash",
            Mode = "log-only",
            Threshold = 90
        };

        var config = new Configuration
        {
            EnforcementEnabled = true,
            HookHandlers = new Dictionary<string, HookEventConfig>
            {
                ["PermissionRequest"] = new HookEventConfig
                {
                    Enabled = true,
                    Handlers = new List<HandlerConfig> { handlerConfig }
                }
            }
        };

        var controller = CreateControllerWithEnforcement(enforcementEnabled: true, customConfig: config);

        var rawInput = ParseJson(@"{
            ""sessionId"": ""test-session-abc123"",
            ""toolName"": ""Bash"",
            ""toolInput"": { ""command"": ""ls"" }
        }");

        // Act
        var result = await controller.HandleClaudeHook("PermissionRequest", rawInput);

        // Assert - log-only handlers return empty JSON (no opinion)
        var json = GetContentString(result);
        Assert.Equal("{}", json);
    }

    // -----------------------------------------------------------------------
    // Additional: Unsupported handler mode => returns empty JSON
    // -----------------------------------------------------------------------
    [Fact]
    public async Task HandleClaudeHook_ReturnsEmptyJson_WhenHandlerModeIsNotSupported()
    {
        // Arrange
        var handlerConfig = new HandlerConfig
        {
            Name = "unknown-handler",
            Matcher = "Bash",
            Mode = "unsupported-mode",
            Threshold = 90,
            AutoApprove = true
        };

        var config = new Configuration
        {
            EnforcementEnabled = true,
            HookHandlers = new Dictionary<string, HookEventConfig>
            {
                ["PermissionRequest"] = new HookEventConfig
                {
                    Enabled = true,
                    Handlers = new List<HandlerConfig> { handlerConfig }
                }
            }
        };

        var controller = CreateControllerWithEnforcement(enforcementEnabled: true, customConfig: config);

        // Factory throws NotSupportedException for unknown mode
        _mockHandlerFactory
            .Setup(f => f.Create("unsupported-mode", It.IsAny<string?>(), It.IsAny<string?>()))
            .Throws(new NotSupportedException("Handler mode 'unsupported-mode' is not supported"));

        _mockProfileService
            .Setup(p => p.GetThresholdForTool(It.IsAny<string?>()))
            .Returns(90);
        _mockProfileService
            .Setup(p => p.IsAutoApproveEnabled())
            .Returns(true);

        var rawInput = ParseJson(@"{
            ""sessionId"": ""test-session-abc123"",
            ""toolName"": ""Bash""
        }");

        // Act
        var result = await controller.HandleClaudeHook("PermissionRequest", rawInput);

        // Assert - unsupported mode returns {} (caught by NotSupportedException handler)
        var json = GetContentString(result);
        Assert.Equal("{}", json);
    }

    // -----------------------------------------------------------------------
    // Additional: Deny with interrupt flag set
    // -----------------------------------------------------------------------
    [Fact]
    public async Task HandleClaudeHook_ReturnsDenyWithInterrupt_WhenHandlerDeniesWithInterrupt()
    {
        // Arrange
        var handlerConfig = new HandlerConfig
        {
            Name = "test-handler",
            Matcher = "Bash",
            Mode = "llm-analysis",
            Threshold = 95,
            AutoApprove = true
        };

        var config = new Configuration
        {
            EnforcementEnabled = true,
            HookHandlers = new Dictionary<string, HookEventConfig>
            {
                ["PermissionRequest"] = new HookEventConfig
                {
                    Enabled = true,
                    Handlers = new List<HandlerConfig> { handlerConfig }
                }
            }
        };

        var controller = CreateControllerWithEnforcement(enforcementEnabled: true, customConfig: config);

        var deniedOutput = new HookOutput
        {
            AutoApprove = false,
            SafetyScore = 10,
            Reasoning = "Extremely dangerous: system file deletion",
            Category = "dangerous",
            Threshold = 95,
            Interrupt = true
        };

        var mockHandler = new Mock<IHookHandler>();
        mockHandler
            .Setup(h => h.HandleAsync(
                It.IsAny<HookInput>(),
                It.IsAny<HandlerConfig>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(deniedOutput);

        _mockHandlerFactory
            .Setup(f => f.Create("llm-analysis", It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(mockHandler.Object);

        _mockProfileService
            .Setup(p => p.GetThresholdForTool(It.IsAny<string?>()))
            .Returns(95);
        _mockProfileService
            .Setup(p => p.IsAutoApproveEnabled())
            .Returns(true);

        var rawInput = ParseJson(@"{
            ""sessionId"": ""test-session-abc123"",
            ""toolName"": ""Bash"",
            ""toolInput"": { ""command"": ""rm -rf /etc"" }
        }");

        // Act
        var result = await controller.HandleClaudeHook("PermissionRequest", rawInput);

        // Assert
        var json = GetContentString(result);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var decision = root.GetProperty("hookSpecificOutput").GetProperty("decision");
        Assert.Equal("deny", decision.GetProperty("behavior").GetString());
        Assert.Contains("Safety score", decision.GetProperty("message").GetString());
    }

    // -----------------------------------------------------------------------
    // Additional: Maps snake_case input fields correctly
    // -----------------------------------------------------------------------
    [Fact]
    public async Task HandleClaudeHook_MapsSnakeCaseFields_Correctly()
    {
        // Arrange - use snake_case field names (session_id, tool_name, tool_input)
        var handlerConfig = new HandlerConfig
        {
            Name = "test-handler",
            Matcher = "Bash",
            Mode = "llm-analysis",
            Threshold = 90,
            AutoApprove = true
        };

        var config = new Configuration
        {
            EnforcementEnabled = true,
            HookHandlers = new Dictionary<string, HookEventConfig>
            {
                ["PermissionRequest"] = new HookEventConfig
                {
                    Enabled = true,
                    Handlers = new List<HandlerConfig> { handlerConfig }
                }
            }
        };

        var controller = CreateControllerWithEnforcement(enforcementEnabled: true, customConfig: config);

        var approvedOutput = new HookOutput
        {
            AutoApprove = true,
            SafetyScore = 99,
            Reasoning = "Safe command",
            Category = "safe",
            Threshold = 90
        };

        var mockHandler = new Mock<IHookHandler>();
        mockHandler
            .Setup(h => h.HandleAsync(
                It.IsAny<HookInput>(),
                It.IsAny<HandlerConfig>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(approvedOutput);

        _mockHandlerFactory
            .Setup(f => f.Create("llm-analysis", It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(mockHandler.Object);

        _mockProfileService
            .Setup(p => p.GetThresholdForTool(It.IsAny<string?>()))
            .Returns(90);
        _mockProfileService
            .Setup(p => p.IsAutoApproveEnabled())
            .Returns(true);

        // Use snake_case field names
        var rawInput = ParseJson(@"{
            ""session_id"": ""test-session-abc123"",
            ""tool_name"": ""Bash"",
            ""tool_input"": { ""command"": ""echo hello"" }
        }");

        // Act
        var result = await controller.HandleClaudeHook("PermissionRequest", rawInput);

        // Assert - should process correctly with snake_case mapping
        var json = GetContentString(result);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var decision = root.GetProperty("hookSpecificOutput").GetProperty("decision");
        Assert.Equal("allow", decision.GetProperty("behavior").GetString());
    }
}
