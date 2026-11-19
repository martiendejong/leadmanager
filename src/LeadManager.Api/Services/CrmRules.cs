using System.Text.Json;
using System.Text.Json.Serialization;

namespace LeadManager.Api.Services;

/// <summary>
/// The statuses a client project can have. Old values stay valid: Active, On Hold and Completed existed first;
/// Concept (internal draft, not yet offered), Pilot, Maintenance (Beheer) and Archived came later.
/// </summary>
public static class ProjectStatuses
{
    public static readonly string[] All = { "Concept", "Pilot", "Active", "Maintenance", "On Hold", "Completed", "Archived" };
}

/// <summary>Input rules shared by the CRM controllers (review fixes for task 8023).</summary>
public static class CrmRules
{
    public const int MaxName = 200;
    public const int MaxTitle = 300;
    public const int MaxLabel = 200;
    public const int MaxOutcome = 100;
    public const int MaxSourceRef = 200;
    public const int MaxUrl = 2000;
    public const int MaxSummary = 8000;
    public const int MaxNotes = 4000;

    public static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    public static bool TooLong(string? v, int max) => v != null && v.Length > max;

    /// <summary>Only absolute http(s) URLs may be stored and rendered as links.</summary>
    public static bool IsSafeHttpUrl(string? url) =>
        url != null && url.Length <= MaxUrl &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Case-insensitive enum parse that only accepts the names. Enum.TryParse also accepts numeric
    /// strings such as "99", which would store a value that is not part of the enum.
    /// </summary>
    public static bool TryParseName<T>(string? value, out T result) where T : struct, Enum
    {
        result = default;
        // Enum.TryParse also accepts numbers ("99") and comma lists ("Call,Email", parsed as flags 0|1).
        if (string.IsNullOrWhiteSpace(value) || char.IsDigit(value.Trim()[0]) || value.Trim()[0] == '-'
            || value.Contains(','))
            return false;
        return Enum.TryParse(value.Trim(), true, out result) && Enum.IsDefined(typeof(T), result);
    }
}

/// <summary>
/// SQLite hands back DateTime with Kind=Unspecified, which System.Text.Json writes without a "Z".
/// Browsers then read the instant as local time and show it hours off. All CRM timestamps are UTC,
/// so they are always written with "Z", and a timestamp without a zone on input is taken as UTC.
/// </summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public static DateTime Normalize(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc),
    };

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Normalize(reader.GetDateTime());

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Normalize(value));
}

public sealed class UtcNullableDateTimeConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : UtcDateTimeConverter.Normalize(reader.GetDateTime());

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteStringValue(UtcDateTimeConverter.Normalize(value.Value));
        else writer.WriteNullValue();
    }
}
