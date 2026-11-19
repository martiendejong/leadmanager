using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LeadManager.Api.Models;

namespace LeadManager.Api.Services.Hunt;

/// <summary>
/// Remembers, per field, which values a hunt or an enrichment wrote (as a short hash of the value at that moment). Only a value whose hash
/// still matches may be cleared again without the person's confirmation: a value that was typed by a person, or edited after the hunt
/// wrote it, has no matching hash and is never cleared by a verify.
/// </summary>
public static class HuntProvenance
{
    public static readonly string[] Fields =
    {
        "ownerName", "ownerLinkedInUrl", "linkedInUrl", "ownerMobile", "personalEmail", "foundingYear", "employeeCount", "legalForm", "phone", "googleRating", "kvkNumber",
    };

    public static string? ValueOf(Lead l, string field) => field switch
    {
        "ownerName" => l.OwnerName,
        "ownerLinkedInUrl" => l.OwnerLinkedInUrl,
        "linkedInUrl" => l.LinkedInUrl,
        "ownerMobile" => l.OwnerMobile,
        "personalEmail" => l.PersonalEmail,
        "foundingYear" => l.FoundingYear?.ToString(CultureInfo.InvariantCulture),
        "employeeCount" => l.EmployeeCount,
        "legalForm" => l.LegalForm,
        "phone" => l.Phone,
        "googleRating" => l.GoogleRating?.ToString("F1", CultureInfo.InvariantCulture),
        "kvkNumber" => l.KvkNumber,
        _ => null,
    };

    public static string Hash(string? value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes((value ?? "").Trim().ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public static Dictionary<string, string> Read(Lead l)
    {
        if (string.IsNullOrWhiteSpace(l.HuntWrittenJson)) return new Dictionary<string, string>();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(l.HuntWrittenJson) ?? new Dictionary<string, string>(); }
        catch (JsonException) { return new Dictionary<string, string>(); }
    }

    private static void Write(Lead l, Dictionary<string, string> map) =>
        l.HuntWrittenJson = map.Count == 0 ? null : JsonSerializer.Serialize(map);

    /// <summary>The server wrote these fields with their current values.</summary>
    public static void Record(Lead l, IEnumerable<string> fields)
    {
        var map = Read(l);
        foreach (var f in fields)
        {
            var v = ValueOf(l, f);
            if (string.IsNullOrWhiteSpace(v)) map.Remove(f);
            else map[f] = Hash(v);
        }
        Write(l, Prune(l, map));
    }

    /// <summary>Entries whose value was cleared or changed since are forgotten.</summary>
    private static Dictionary<string, string> Prune(Lead l, Dictionary<string, string> map) =>
        map.Where(kv => Fields.Contains(kv.Key) && !string.IsNullOrWhiteSpace(ValueOf(l, kv.Key)) && Hash(ValueOf(l, kv.Key)) == kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);

    public static void Reconcile(Lead l) => Write(l, Prune(l, Read(l)));

    /// <summary>True only when this field was written by the server and nobody changed it since.</summary>
    public static bool Unchanged(Lead l, string field)
    {
        var v = ValueOf(l, field);
        return !string.IsNullOrWhiteSpace(v) && Read(l).TryGetValue(field, out var hash) && hash == Hash(v);
    }

    public static Dictionary<string, string?> Snapshot(Lead l) => Fields.ToDictionary(f => f, f => ValueOf(l, f));

    /// <summary>Fields that now hold a value that differs from the snapshot were written in between (by an enrichment): record them.</summary>
    public static void RecordChanges(Lead l, Dictionary<string, string?> before)
    {
        var changed = Fields.Where(f =>
        {
            var now = ValueOf(l, f);
            return !string.IsNullOrWhiteSpace(now) && !string.Equals((before.GetValueOrDefault(f) ?? "").Trim(), now.Trim(), StringComparison.OrdinalIgnoreCase);
        }).ToList();
        Record(l, changed);
    }
}
