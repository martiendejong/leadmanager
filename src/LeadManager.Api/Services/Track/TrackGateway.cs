using System.Text.Json;
using JengoTrack;
using Microsoft.Extensions.Options;

namespace LeadManager.Api.Services.Track;

public record TrackClickDto(long Ts, string? Url, string? Label, string? Campaign, string? Channel, string? Device, string? Reason);

/// <summary>What the lead or client screen shows in the "Klikken" section.</summary>
public record TrackTimelineDto(
    bool Enabled, bool Available, int HumanClickCount,
    IReadOnlyList<TrackClickDto> Human, IReadOnlyList<TrackClickDto> Machine);

/// <summary>Thin wrapper around JengoTrackClient: every failure becomes "nothing", never an exception.</summary>
public class TrackGateway
{
    private readonly TrackOptions _options;
    private readonly HttpClient _http;

    public TrackGateway(IOptions<TrackOptions> options, HttpClient http)
    {
        _options = options.Value;
        _http = http;
    }

    public bool Enabled => _options.Enabled;

    private JengoTrackClient Client() => new(_http, _options.BaseUrl, _options.ApiKey);

    public async Task<TrackTimelineDto> GetTimelineAsync(string entityType, Guid entityId, CancellationToken ct = default)
    {
        var empty = new List<TrackClickDto>();
        if (!_options.Enabled) return new TrackTimelineDto(false, false, 0, empty, empty);
        if (!_options.CanCallTrack) return new TrackTimelineDto(true, false, 0, empty, empty);

        var root = await Client().GetTimelineAsync(entityType, entityId.ToString(), ct);
        if (root is not { ValueKind: JsonValueKind.Object } json) return new TrackTimelineDto(true, false, 0, empty, empty);

        var human = new List<TrackClickDto>();
        var machine = new List<TrackClickDto>();
        if (json.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in events.EnumerateArray())
            {
                if (Str(e, "kind") != "click") continue;
                var dto = new TrackClickDto(Num(e, "ts"), Str(e, "url"), Str(e, "label"), Str(e, "campaign"),
                    Str(e, "channel"), Str(e, "device"), Str(e, "bot_reason"));
                var isHuman = e.TryGetProperty("is_human", out var h) && h.ValueKind == JsonValueKind.True;
                (isHuman ? human : machine).Add(dto);
            }
        }
        return new TrackTimelineDto(true, true, human.Count, human, machine);
    }

    /// <summary>Creates a tracked link; null when Track is unreachable or refuses the destination.</summary>
    public Task<TrackLink?> CreateLinkAsync(LinkRequest request, CancellationToken ct = default) =>
        _options.CanCallTrack ? Client().CreateLinkAsync(request, ct) : Task.FromResult<TrackLink?>(null);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
