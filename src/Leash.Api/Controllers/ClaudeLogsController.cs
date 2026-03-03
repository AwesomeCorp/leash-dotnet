using Leash.Api.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Leash.Api.Controllers;

[ApiController]
[Route("api/claude-logs")]
[Route("api/transcripts")]
public class ClaudeLogsController : ControllerBase
{
    private readonly TranscriptWatcher _transcriptWatcher;
    private readonly ILogger<ClaudeLogsController> _logger;

    public ClaudeLogsController(
        TranscriptWatcher transcriptWatcher,
        ILogger<ClaudeLogsController> logger)
    {
        _transcriptWatcher = transcriptWatcher;
        _logger = logger;
    }

    [HttpGet("projects")]
    public IActionResult GetProjects()
    {
        try
        {
            var projects = _transcriptWatcher.GetProjects();
            return Ok(projects);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list projects");
            return StatusCode(500, new { error = "Failed to list projects" });
        }
    }

    [HttpGet("transcript/{sessionId}")]
    public IActionResult GetTranscript(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return BadRequest(new { error = "SessionId is required" });

        // Validate sessionId to prevent path traversal
        if (sessionId.Contains("..") || sessionId.Contains('/') || sessionId.Contains('\\'))
            return BadRequest(new { error = "Invalid session ID" });

        try
        {
            var entries = _transcriptWatcher.GetTranscript(sessionId);
            return Ok(entries);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get transcript for session {SessionId}", sessionId);
            return StatusCode(500, new { error = "Failed to get transcript" });
        }
    }

    [HttpGet("transcript/{sessionId}/stream")]
    public async Task StreamTranscript(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            Response.StatusCode = 400;
            return;
        }

        // Validate sessionId to prevent path traversal
        if (sessionId.Contains("..") || sessionId.Contains('/') || sessionId.Contains('\\'))
        {
            Response.StatusCode = 400;
            return;
        }

        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";

        var tcs = new TaskCompletionSource<bool>();

        void OnTranscriptUpdated(object? sender, TranscriptEventArgs args)
        {
            if (args.SessionId != sessionId)
                return;

            try
            {
                foreach (var entry in args.NewEntries)
                {
                    var json = JsonSerializer.Serialize(entry);
                    var sseData = $"data: {json}\n\n";
                    var bytes = System.Text.Encoding.UTF8.GetBytes(sseData);
                    Response.Body.WriteAsync(bytes, 0, bytes.Length);
                    Response.Body.FlushAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to write SSE data for session {SessionId}", sessionId);
            }
        }

        _transcriptWatcher.TranscriptUpdated += OnTranscriptUpdated;

        try
        {
            // Send initial keepalive
            await Response.WriteAsync(": connected\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);

            // Keep the connection open until cancelled
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
        finally
        {
            _transcriptWatcher.TranscriptUpdated -= OnTranscriptUpdated;
        }
    }
}
