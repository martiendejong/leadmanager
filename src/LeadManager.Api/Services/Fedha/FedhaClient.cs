using System.Text.Json;
using System.Text.Json.Serialization;

namespace LeadManager.Api.Services.Fedha;

/// <summary>Settings for the read-only link to Fedha. The key comes from the environment (Fedha__ApiKey), never from a file in git.</summary>
public class FedhaOptions
{
    public string BaseUrl { get; set; } = "https://fedha.martiendejong.nl/api";
    public string? ApiKey { get; set; }
    /// <summary>E-mail of the Clients user whose clients receive the synced invoices (used by the daily job).</summary>
    public string? SyncUserEmail { get; set; }
    public bool Configured => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>An invoice as the Fedha list endpoint reports it. Status: 1 Draft, 2 Sent, 3 Paid, 4 Overdue, 5 Cancelled.</summary>
public record FedhaInvoiceRow(
    int Id, int ContactId, string ContactName, string InvoiceNumber, DateTime InvoiceDate, int Status,
    string? Description, decimal Subtotal, decimal VatAmount, decimal Total, decimal OutstandingAmount,
    DateTime? PaidDate, DateTime? LastSentAt, int DaysOverdue);

public interface IFedhaClient
{
    Task<IReadOnlyList<FedhaInvoiceRow>> GetInvoicesAsync(CancellationToken ct);
}

/// <summary>Reads invoices from Fedha with a read-only X-API-Key. Fedha stays the owner of invoicing: nothing is ever written back.</summary>
public class HttpFedhaClient : IFedhaClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.AllowReadingFromString };
    private readonly HttpClient _http;
    private readonly FedhaOptions _opt;

    public HttpFedhaClient(HttpClient http, FedhaOptions opt) { _http = http; _opt = opt; }

    public async Task<IReadOnlyList<FedhaInvoiceRow>> GetInvoicesAsync(CancellationToken ct)
    {
        if (!_opt.Configured) throw new InvalidOperationException("Fedha is not configured (Fedha__ApiKey is empty).");
        using var req = new HttpRequestMessage(HttpMethod.Get, _opt.BaseUrl.TrimEnd('/') + "/invoices?pageSize=1000");
        req.Headers.Add("X-API-Key", _opt.ApiKey);
        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Fedha answered {(int)res.StatusCode} on /invoices.");
        var rows = await res.Content.ReadFromJsonAsync<List<FedhaInvoiceRow>>(Json, ct);
        return rows ?? new List<FedhaInvoiceRow>();
    }
}
