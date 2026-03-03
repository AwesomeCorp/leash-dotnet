using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Leash.Api.Controllers;

[ApiController]
[Route("api/copilot-settings")]
public class CopilotSettingsController : ControllerBase
{
    private readonly ILogger<CopilotSettingsController> _logger;

    /// <summary>
    /// Returns the path to the user-level Copilot hooks.json.
    /// This is the global hooks config at ~/.copilot/hooks/hooks.json.
    /// </summary>
    private static string GetSettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".copilot",
        "hooks",
        "hooks.json");

    public CopilotSettingsController(ILogger<CopilotSettingsController> logger)
    {
        _logger = logger;
    }

    [HttpGet]
    public IActionResult GetSettings()
    {
        try
        {
            var settingsPath = GetSettingsPath();
            if (!System.IO.File.Exists(settingsPath))
                return Ok(new { path = settingsPath, exists = false, content = "{}" });

            var json = System.IO.File.ReadAllText(settingsPath);
            // Validate it's valid JSON
            JsonNode.Parse(json);
            return Ok(new { path = settingsPath, exists = true, content = json });
        }
        catch (JsonException)
        {
            var settingsPath = GetSettingsPath();
            var raw = System.IO.File.ReadAllText(settingsPath);
            return Ok(new { path = settingsPath, exists = true, content = raw, parseError = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read Copilot settings");
            return StatusCode(500, new { error = "Failed to read settings: " + ex.Message });
        }
    }

    [HttpPut]
    [Consumes("application/json")]
    public IActionResult SaveSettings([FromBody] JsonElement body)
    {
        try
        {
            if (!body.TryGetProperty("content", out var contentElement))
                return BadRequest(new { error = "content field is required" });

            var content = contentElement.GetString();
            if (content == null)
                return BadRequest(new { error = "content must be a string" });

            // Validate JSON before saving
            var parsed = JsonNode.Parse(content);
            if (parsed == null)
                return BadRequest(new { error = "content is not valid JSON" });

            // Pretty-print
            var pretty = parsed.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            var settingsPath = GetSettingsPath();
            var dir = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            System.IO.File.WriteAllText(settingsPath, pretty);
            _logger.LogInformation("Copilot hooks.json updated via web UI");

            return Ok(new { saved = true, path = settingsPath });
        }
        catch (JsonException ex)
        {
            return BadRequest(new { error = "Invalid JSON: " + ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save Copilot settings");
            return StatusCode(500, new { error = "Failed to save: " + ex.Message });
        }
    }
}
