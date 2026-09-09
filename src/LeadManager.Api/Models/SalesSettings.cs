namespace LeadManager.Api.Models;

public class SalesSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";

    // ── Bedrijfsgegevens (voor offertes) ──────────────────────────────
    public string CompanyName { get; set; } = "";
    public string? CompanyAddress { get; set; }
    public string? CompanyCity { get; set; }
    public string? CompanyZipCode { get; set; }
    public string? CompanyKvk { get; set; }
    public string? CompanyVat { get; set; }
    public string? CompanyIban { get; set; }
    public string? CompanyEmail { get; set; }
    public string? CompanyPhone { get; set; }
    public string? CompanyWebsite { get; set; }

    // ── Prijzen (overrule defaults) ───────────────────────────────────
    public decimal StarterHours { get; set; } = 50m;
    public decimal StarterMonthlyPrice { get; set; } = 125m;
    public decimal TeamHours { get; set; } = 200m;
    public decimal TeamMonthlyPrice { get; set; } = 500m;
    public decimal BundleHourlyRate { get; set; } = 2.50m;
    public decimal LooseHourlyRate { get; set; } = 3.00m;
    public decimal OverageHourlyRate { get; set; } = 3.00m;

    // ── Offerte instellingen ──────────────────────────────────────────
    public string QuoteNumberPrefix { get; set; } = "OFF";
    public int QuoteNumberCurrent { get; set; } = 1001;
    public int QuoteValidityDays { get; set; } = 30;
    public string? QuoteIntroText { get; set; }
    public string? QuoteTerms { get; set; }
    public string? QuoteFooterText { get; set; }

    // ── Belscript template (JSON-secties) ─────────────────────────────
    // Stored as JSON: array of { id, title, prompt, defaultText }
    public string? CallScriptSectionsJson { get; set; }

    // ── Email template ────────────────────────────────────────────────
    public string? EmailSubjectTemplate { get; set; }
    public string? EmailBodyTemplate { get; set; }

    // ── Fedha koppeling ───────────────────────────────────────────────
    public string? FedhaBaseUrl { get; set; }
    public string? FedhaApiKey { get; set; }
    public string? FedhaDefaultProjectId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
