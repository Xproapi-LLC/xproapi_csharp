using System.Text.Json.Nodes;

namespace Xproapi;

/// <summary>
/// Any error answer from the API ({"status":"error","error":&lt;code&gt;,"message":"..."}) or a network failure.
/// Catch a subclass to handle one case, or XproapiException for all of them.
/// </summary>
public class XproapiException : Exception
{
    public XproapiException(string message, int status = 0, JsonObject? body = null, int? retryAfter = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Status = status;
        Body = body;
        RetryAfter = retryAfter;
    }

    /// <summary>HTTP status (0 for network errors).</summary>
    public int Status { get; }

    /// <summary>The decoded JSON error body, when there was one.</summary>
    public JsonObject? Body { get; }

    /// <summary>Seconds to wait before retrying (429 only).</summary>
    public int? RetryAfter { get; }

    /// <summary>The matching exception for an HTTP status.</summary>
    internal static XproapiException FromResponse(int status, JsonObject? body, int? retryAfter)
    {
        var message = body?["message"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : $"Request failed with HTTP {status}.";

        return status switch
        {
            400 => new BadRequestException(message, status, body, retryAfter),
            401 => new AuthenticationException(message, status, body, retryAfter),
            402 => new InsufficientCreditsException(message, status, body, retryAfter),
            403 => new PermissionDeniedException(message, status, body, retryAfter),
            404 => new NotFoundException(message, status, body, retryAfter),
            429 => new RateLimitException(message, status, body, retryAfter),
            >= 500 => new ServiceUnavailableException(message, status, body, retryAfter),
            _ => new XproapiException(message, status, body, retryAfter),
        };
    }
}

/// <summary>400: a missing or invalid parameter.</summary>
public class BadRequestException(string message, int status, JsonObject? body, int? retryAfter) : XproapiException(message, status, body, retryAfter);

/// <summary>401: missing or invalid X-API-Key.</summary>
public class AuthenticationException(string message, int status, JsonObject? body, int? retryAfter) : XproapiException(message, status, body, retryAfter);

/// <summary>402: the balance can't cover the request. Nothing was charged.</summary>
public class InsufficientCreditsException(string message, int status, JsonObject? body, int? retryAfter) : XproapiException(message, status, body, retryAfter);

/// <summary>403: the account can't use the API, or the X account is protected.</summary>
public class PermissionDeniedException(string message, int status, JsonObject? body, int? retryAfter) : XproapiException(message, status, body, retryAfter);

/// <summary>404: the user, tweet or article doesn't exist (or isn't visible).</summary>
public class NotFoundException(string message, int status, JsonObject? body, int? retryAfter) : XproapiException(message, status, body, retryAfter);

/// <summary>429: too many requests. RetryAfter says how many seconds to wait.</summary>
public class RateLimitException(string message, int status, JsonObject? body, int? retryAfter) : XproapiException(message, status, body, retryAfter);

/// <summary>5xx: X or the API is temporarily unavailable. Safe to retry later.</summary>
public class ServiceUnavailableException(string message, int status, JsonObject? body, int? retryAfter) : XproapiException(message, status, body, retryAfter);
