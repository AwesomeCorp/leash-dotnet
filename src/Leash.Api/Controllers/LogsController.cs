using Leash.Api.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;

namespace Leash.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LogsController : ControllerBase
{
    private readonly SessionManager _sessionManager;
    private readonly ILogger<LogsController> _logger;

    public LogsController(
        SessionManager sessionManager,
        ILogger<LogsController> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetLogs(
        [FromQuery] int limit = 100,
        [FromQuery] string? decision = null,
        [FromQuery] string? category = null,
        [FromQuery] string? sessionId = null,
        [FromQuery] string? toolName = null,
        [FromQuery] string? hookType = null,
        [FromQuery] string? provider = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            limit = Math.Clamp(limit, 1, 1000);
            var sessions = await _sessionManager.GetAllSessionsAsync(cancellationToken);

            var events = sessions
                .Where(s => string.IsNullOrEmpty(sessionId) || s.SessionId == sessionId)
                .SelectMany(s => s.ConversationHistory.Select(e => new
                {
                    e.Timestamp,
                    e.Type,
                    e.ToolName,
                    e.ToolInput,
                    e.Decision,
                    e.SafetyScore,
                    e.Reasoning,
                    e.Category,
                    e.Content,
                    e.HandlerName,
                    e.PromptTemplate,
                    e.Threshold,
                    e.ElapsedMs,
                    e.ResponseJson,
                    e.Provider,
                    SessionId = s.SessionId
                }))
                .Where(e => { var f = ParseFilterValues(decision); return f == null || (e.Decision != null && f.Contains(e.Decision)); })
                .Where(e => { var f = ParseFilterValues(category); return f == null || (e.Category != null && f.Contains(e.Category)); })
                .Where(e => { var f = ParseFilterValues(toolName); return f == null || (e.ToolName != null && f.Any(v => e.ToolName.Contains(v, StringComparison.OrdinalIgnoreCase))); })
                .Where(e => { var f = ParseFilterValues(hookType); return f == null || (e.Type != null && f.Contains(e.Type)); })
                .Where(e => { var f = ParseFilterValues(provider); return f == null || f.Contains(e.Provider ?? "claude"); })
                .OrderByDescending(e => e.Timestamp)
                .Take(limit)
                .ToList();

            return Ok(events);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve logs");
            return StatusCode(500, new { error = "Failed to load logs" });
        }
    }

    [HttpDelete]
    public async Task<IActionResult> ClearLogs(CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await _sessionManager.ClearAllSessionsAsync(cancellationToken);
            return Ok(new { cleared = deleted, message = $"Cleared {deleted} session(s)" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear logs");
            return StatusCode(500, new { error = "Failed to clear logs" });
        }
    }

    [HttpGet("export/json")]
    public async Task<IActionResult> ExportJson(CancellationToken cancellationToken)
    {
        try
        {
            var sessions = await _sessionManager.GetAllSessionsAsync(cancellationToken);
            var events = sessions
                .SelectMany(s => s.ConversationHistory.Select(e => new
                {
                    e.Timestamp,
                    e.Type,
                    e.ToolName,
                    e.ToolInput,
                    e.Decision,
                    e.SafetyScore,
                    e.Reasoning,
                    e.Category,
                    e.Content,
                    e.HandlerName,
                    e.PromptTemplate,
                    e.Threshold,
                    e.ElapsedMs,
                    e.ResponseJson,
                    e.Provider,
                    SessionId = s.SessionId
                }))
                .OrderByDescending(e => e.Timestamp)
                .ToList();

            var json = JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true });
            var bytes = Encoding.UTF8.GetBytes(json);

            return File(bytes, "application/json", $"permission-logs-{DateTime.UtcNow:yyyy-MM-dd}.json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export logs as JSON");
            return StatusCode(500, new { error = "Failed to export logs" });
        }
    }

    [HttpGet("export/csv")]
    public async Task<IActionResult> ExportCsv(CancellationToken cancellationToken)
    {
        try
        {
            var sessions = await _sessionManager.GetAllSessionsAsync(cancellationToken);
            var events = sessions
                .SelectMany(s => s.ConversationHistory.Select(e => new
                {
                    e.Timestamp,
                    e.Type,
                    e.ToolName,
                    e.ToolInput,
                    e.Decision,
                    e.SafetyScore,
                    e.Reasoning,
                    e.Category,
                    e.Content,
                    e.HandlerName,
                    e.PromptTemplate,
                    e.Threshold,
                    e.ElapsedMs,
                    e.ResponseJson,
                    e.Provider,
                    SessionId = s.SessionId
                }))
                .OrderByDescending(e => e.Timestamp)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("Timestamp,SessionId,Type,ToolName,Decision,SafetyScore,Category,Provider,Reasoning");

            foreach (var evt in events)
            {
                var reasoning = EscapeCsvField(evt.Reasoning ?? "");
                var toolName = EscapeCsvField(evt.ToolName ?? "");
                sb.AppendLine($"{evt.Timestamp:O},{evt.SessionId},{evt.Type},{toolName},{evt.Decision},{evt.SafetyScore},{evt.Category},{evt.Provider},{reasoning}");
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            return File(bytes, "text/csv", $"permission-logs-{DateTime.UtcNow:yyyy-MM-dd}.csv");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export logs as CSV");
            return StatusCode(500, new { error = "Failed to export logs" });
        }
    }

    private static HashSet<string>? ParseFilterValues(string? param)
    {
        if (string.IsNullOrWhiteSpace(param)) return null;
        return param.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string EscapeCsvField(string field)
    {
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
        return field;
    }
}
