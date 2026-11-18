namespace LeadManager.Api.Services.Track;

/// <summary>
/// Settings for the Jengo Track integration (JengoWork task 8071). Section "JengoTrack".
/// Off by default; secrets come from configuration or environment only (JengoTrack__ApiKey,
/// JengoTrack__WebhookSecret), never from a committed file.
/// </summary>
public class TrackOptions
{
    public const string Section = "JengoTrack";

    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    /// <summary>Human clicks on one entity at which a "Bel nu" action is planned.</summary>
    public int HotThreshold { get; set; } = 2;

    /// <summary>Enabled and complete enough to call Track.</summary>
    public bool CanCallTrack => Enabled && !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}
