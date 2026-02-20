using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text.Json;
using Gelato.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Gelato.Controllers;

/// <summary>
/// Palco API - Simple key-value cache migrated to Gelato.
/// Maintains the /Palco route for compatibility.
/// </summary>
[ApiController]
[Route("Palco")]
[Authorize]
public class PalcoCacheController : ControllerBase
{
    private readonly ILogger<PalcoCacheController> _logger;
    private const string RegistrationNs = "anfiteatro-registration";

    // Access the service via GelatoPlugin instance or injection
    private PalcoCacheService? Cache => GelatoPlugin.Instance?.PalcoCache;

    public PalcoCacheController(ILogger<PalcoCacheController> logger)
    {
        _logger = logger;
    }

    // ========== PUBLIC ENDPOINTS (No Auth) ==========

    /// <summary>
    /// Check if registration is enabled.
    /// </summary>
    [HttpGet("Registration/Enabled")]
    [AllowAnonymous]
    public ActionResult GetRegistrationEnabled()
    {
        var value = Cache?.Get("registration-enabled", RegistrationNs);
        var enabled = string.IsNullOrEmpty(value) || JsonSerializer.Deserialize<bool>(value);
        return Ok(new { enabled });
    }

    /// <summary>
    /// Submit a registration request.
    /// </summary>
    [HttpPost("Registration/Request")]
    [AllowAnonymous]
    [Consumes(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> SubmitRegistrationRequest([FromBody] RegistrationRequest request)
    {
        if (Cache == null) return StatusCode(503, new { error = "Cache unavailable" });
        if (string.IsNullOrEmpty(request.Id) || !request.Id.StartsWith("request-"))
            return BadRequest(new { error = "Invalid request ID" });

        // Store request
        Cache.Set(request.Id, request.Data, request.TtlSeconds, RegistrationNs);
        _logger.LogInformation("[Gelato] Palco Registration request received: {Id}", request.Id);

        // Update request index for listing
        var indexJson = Cache.Get("requests-index", RegistrationNs);
        var index = string.IsNullOrEmpty(indexJson)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(indexJson) ?? new List<string>();

        var requestId = request.Id.Replace("request-", "");
        if (!index.Contains(requestId))
        {
            index.Add(requestId);
            Cache.Set("requests-index", JsonSerializer.Serialize(index), 0, RegistrationNs);
            _logger.LogInformation("[Gelato] Palco Request added to index: {Id}", requestId);
        }

        // Notify admin via email if SMTP is configured
        try
        {
            var smtpJson = Cache.Get("smtp-config", RegistrationNs);
            if (!string.IsNullOrEmpty(smtpJson))
            {
                var smtp = JsonSerializer.Deserialize<SmtpConfig>(smtpJson);
                if (smtp != null && !string.IsNullOrEmpty(smtp.Host))
                {
                    // Extract user details
                    var userData = JsonSerializer.Deserialize<JsonElement>(request.Data);
                    var userName = userData.TryGetProperty("name", out var n) ? n.GetString() : "Unknown";
                    var userEmail = userData.TryGetProperty("email", out var e) ? e.GetString() : "Unknown";
                    var userMessage = userData.TryGetProperty("userMessage", out var m) ? m.GetString() : "";

                    var adminEmail = !string.IsNullOrEmpty(smtp.AdminEmail) ? smtp.AdminEmail : smtp.Username;

                    if (!string.IsNullOrEmpty(adminEmail))
                    {
                        using var client = new SmtpClient(smtp.Host, smtp.Port)
                        {
                            EnableSsl = true,
                            Credentials = new NetworkCredential(smtp.Username, smtp.Password)
                        };

                        var body = $"A new user has requested access to your server.\n\nUsername: {userName}\nEmail: {userEmail}";

                        if (!string.IsNullOrEmpty(userMessage))
                        {
                            body += $"\n\nMessage from User:\n{userMessage}";
                        }

                        body += "\n\nPlease review this request in the Anfiteatro admin panel.";

                        var mail = new MailMessage
                        {
                            From = new MailAddress(smtp.FromAddress ?? smtp.Username, smtp.FromName ?? "Anfiteatro"),
                            Subject = $"New Registration Request: {userName}",
                            Body = body
                        };
                        mail.To.Add(adminEmail);

                        await client.SendMailAsync(mail);
                        _logger.LogInformation("[Gelato] Palco Admin notification sent to {AdminEmail} for registration: {Id}", adminEmail, requestId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Gelato] Palco Failed to send admin notification email");
        }

        return Ok(new { success = true, requestId });
    }

    // ========== AUTHENTICATED ENDPOINTS ==========

    /// <summary>
    /// Get a cached value.
    /// </summary>
    [HttpGet("Cache/{key}")]
    public ActionResult Get([FromRoute] string key, [FromQuery] string ns = "")
    {
        if (Cache == null) return StatusCode(503);
        var value = Cache.Get(key, ns);
        if (value == null) return NotFound();
        return Ok(new { Key = key, Value = value, Namespace = ns });
    }

    /// <summary>
    /// Set a cached value.
    /// </summary>
    [HttpPost("Cache/{key}")]
    [Consumes(MediaTypeNames.Application.Json)]
    public ActionResult Set([FromRoute] string key, [FromBody] SetRequest request, [FromQuery] string ns = "")
    {
        if (Cache == null) return StatusCode(503);
        Cache.Set(key, request.Value, request.TtlSeconds, ns);
        _logger.LogInformation("[Gelato] Palco Saved: {Key} in {Ns}", key, ns);
        return Ok(new { success = true });
    }

    /// <summary>
    /// Delete a cached value.
    /// </summary>
    [HttpDelete("Cache/{key}")]
    public ActionResult Delete([FromRoute] string key, [FromQuery] string ns = "")
    {
        if (Cache == null) return StatusCode(503);
        var deleted = Cache.Delete(key, ns);

        // If deleting a request, also remove from index
        if (key.StartsWith("request-") && ns == RegistrationNs)
        {
            var requestId = key.Replace("request-", "");
            var indexJson = Cache.Get("requests-index", RegistrationNs);
            if (!string.IsNullOrEmpty(indexJson))
            {
                var index = JsonSerializer.Deserialize<List<string>>(indexJson) ?? new List<string>();
                if (index.Remove(requestId))
                {
                    Cache.Set("requests-index", JsonSerializer.Serialize(index), 0, RegistrationNs);
                    _logger.LogInformation("[Gelato] Palco Request removed from index: {Id}", requestId);
                }
            }
        }

        _logger.LogInformation("[Gelato] Palco Deleted: {Key} from {Ns}, success={Deleted}", key, ns, deleted);
        return Ok(new { success = true, deleted });
    }

    /// <summary>
    /// Get multiple cached values.
    /// </summary>
    [HttpPost("Cache/Bulk")]
    [Consumes(MediaTypeNames.Application.Json)]
    public ActionResult<Dictionary<string, string>> GetBulk([FromBody] BulkRequest request, [FromQuery] string ns = "")
    {
        if (Cache == null) return StatusCode(503);
        return Ok(Cache.GetBulk(request.Keys, ns));
    }

    /// <summary>
    /// Get cache stats.
    /// </summary>
    [HttpGet("Cache/Stats")]
    public ActionResult GetStats()
    {
        if (Cache == null) return StatusCode(503);
        var (total, expired, size) = Cache.GetStats();
        return Ok(new { TotalEntries = total, ExpiredEntries = expired, DatabaseSizeBytes = size });
    }

    /// <summary>
    /// Send an email (uses SMTP config from cache).
    /// </summary>
    [HttpPost("Email/Send")]
    [Consumes(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> SendEmail([FromBody] EmailRequest request)
    {
        try
        {
            using var client = new SmtpClient(request.SmtpHost, request.SmtpPort)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(request.SmtpUsername, request.SmtpPassword)
            };

            var mail = new MailMessage
            {
                From = new MailAddress(request.FromAddress, request.FromName),
                Subject = request.Subject,
                Body = request.Body,
                IsBodyHtml = request.Body.Contains("<")
            };
            mail.To.Add(request.To);

            await client.SendMailAsync(mail);
            _logger.LogInformation("[Gelato] Palco Email sent to {To}", request.To);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Gelato] Palco Email failed to {To}", request.To);
            return Ok(new { success = false, error = ex.Message });
        }
    }
}

#region Request Models

public class RegistrationRequest
{
    [Required] public required string Id { get; set; }
    [Required] public required string Data { get; set; }
    public int TtlSeconds { get; set; } = 2592000; // 30 days
}

public class SetRequest
{
    [Required] public required string Value { get; set; }
    public int TtlSeconds { get; set; } = 0;
}

public class BulkRequest
{
    [Required] public required IEnumerable<string> Keys { get; set; }
}

public class EmailRequest
{
    [Required] public required string To { get; set; }
    [Required] public required string Subject { get; set; }
    [Required] public required string Body { get; set; }
    [Required] public required string SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    [Required] public required string SmtpUsername { get; set; }
    [Required] public required string SmtpPassword { get; set; }
    [Required] public required string FromAddress { get; set; }
    public string FromName { get; set; } = "Anfiteatro";
}

public class SmtpConfig
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? FromAddress { get; set; }
    public string? FromName { get; set; }
    public string? AdminEmail { get; set; }
}

#endregion
