using LeadManager.Api.Models;

namespace LeadManager.Api.Services.Enrichment;

public class LeadSignal
{
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
    public string Severity { get; set; } = "info"; // "info" | "warning" | "alert"
}

public class SignalsGeneratorService
{
    private static readonly string[] FreeEmailDomains = ["gmail.com", "hotmail.com", "outlook.com", "yahoo.com", "live.com", "icloud.com", "hotmail.nl", "gmail.nl"];

    public List<LeadSignal> Generate(Lead lead)
    {
        var signals = new List<LeadSignal>();

        // Free email provider detected
        if (!string.IsNullOrWhiteSpace(lead.CompanyEmail))
        {
            var domain = lead.CompanyEmail.Split('@').LastOrDefault()?.ToLowerInvariant();
            if (domain != null && FreeEmailDomains.Contains(domain))
                signals.Add(new LeadSignal { Type = "email_provider", Message = $"Uses {domain} — no professional domain", Severity = "info" });
        }

        // Website unreachable
        if (lead.WebsiteStatus == WebsiteStatus.Unreachable)
            signals.Add(new LeadSignal { Type = "no_website", Message = "Website not reachable — cold call only", Severity = "alert" });

        // No website at all
        if (string.IsNullOrWhiteSpace(lead.Website))
            signals.Add(new LeadSignal { Type = "no_website", Message = "No website listed — minimal online presence", Severity = "warning" });

        // Part of a group
        if (lead.IsPartOfGroup && !string.IsNullOrWhiteSpace(lead.GroupName))
            signals.Add(new LeadSignal { Type = "group_company", Message = $"Part of {lead.GroupName} group — budget decisions may be centralized", Severity = "warning" });

        // Sole proprietor
        if (!string.IsNullOrWhiteSpace(lead.LegalForm) && (lead.LegalForm.ToLowerInvariant().Contains("eenmanszaak") || lead.LegalForm.ToLowerInvariant().Contains("zelfstandig")))
            signals.Add(new LeadSignal { Type = "solo_operator", Message = "Sole proprietor — owner is the decision maker", Severity = "info" });

        // Young company (founded in last 3 years)
        if (lead.FoundingYear.HasValue && lead.FoundingYear.Value >= DateTime.UtcNow.Year - 3)
            signals.Add(new LeadSignal { Type = "young_company", Message = $"Founded {lead.FoundingYear} — may be actively growing and open to new services", Severity = "info" });

        // Strong Google reputation
        if (lead.GoogleRating.HasValue && lead.GoogleRating >= 4.5f && lead.GoogleReviewCount.HasValue)
            signals.Add(new LeadSignal { Type = "strong_reputation", Message = $"{lead.GoogleRating:F1}/5 Google rating ({lead.GoogleReviewCount} reviews)", Severity = "info" });

        // No LinkedIn found
        if (string.IsNullOrWhiteSpace(lead.LinkedInUrl) && string.IsNullOrWhiteSpace(lead.OwnerLinkedInUrl))
            signals.Add(new LeadSignal { Type = "no_linkedin", Message = "No LinkedIn found — harder to warm up digitally", Severity = "warning" });

        // No mobile / no direct contact
        var hasMobile = !string.IsNullOrWhiteSpace(lead.OwnerMobile) ||
                        (!string.IsNullOrWhiteSpace(lead.Phone) && lead.Phone.Contains("06"));
        if (!hasMobile && string.IsNullOrWhiteSpace(lead.PersonalEmail))
            signals.Add(new LeadSignal { Type = "no_direct_contact", Message = "No mobile or personal email — only general contact available", Severity = "warning" });

        return signals;
    }
}
