using LeadManager.Api.Models;

namespace LeadManager.Api.Services.Enrichment;

public record SalesScoreResult(int Score, string Label, string Reasoning);

public class SalesScoreService
{
    public SalesScoreResult CalculateScore(Lead lead)
    {
        var score = 0;
        var factors = new List<string>();

        // Owner LinkedIn (personal): +2 points
        if (!string.IsNullOrWhiteSpace(lead.OwnerLinkedInUrl))
        { score += 2; factors.Add("Owner LinkedIn known"); }
        // Company LinkedIn: +1 point
        else if (!string.IsNullOrWhiteSpace(lead.LinkedInUrl))
        { score += 1; factors.Add("Company LinkedIn known"); }

        // Mobile phone present: +2 points
        var hasMobile = !string.IsNullOrWhiteSpace(lead.OwnerMobile) ||
                        (!string.IsNullOrWhiteSpace(lead.Phone) && lead.Phone.Contains("06"));
        if (hasMobile)
        { score += 2; factors.Add("Mobile phone available"); }

        // Owner name known: +1 point
        if (!string.IsNullOrWhiteSpace(lead.OwnerName))
        { score += 1; factors.Add("Owner name known"); }

        // High Google rating: +1 point
        if (lead.GoogleRating >= 4.5f)
        { score += 1; factors.Add($"Strong Google rating ({lead.GoogleRating:F1})"); }

        // Small company: +1 point
        if (IsSmallCompany(lead))
        { score += 1; factors.Add("Small company — direct decision maker"); }

        // Established company: +1 point
        if (lead.FoundingYear.HasValue && lead.FoundingYear.Value <= 2015)
        { score += 1; factors.Add($"Established ({lead.FoundingYear})"); }

        // Personal email known: +1 point
        if (!string.IsNullOrWhiteSpace(lead.PersonalEmail))
        { score += 1; factors.Add("Personal email known"); }

        // Unreachable website: -1 point
        if (lead.WebsiteStatus == WebsiteStatus.Unreachable)
        { score -= 1; factors.Add("Website unreachable"); }

        score = Math.Max(0, Math.Min(10, score));
        var label = score >= 7 ? "High" : score >= 4 ? "Medium" : "Low";
        var reasoning = factors.Count > 0 ? string.Join(", ", factors) : "No qualifying factors";

        return new SalesScoreResult(score, label, reasoning);
    }

    private static bool IsSmallCompany(Lead lead)
    {
        if (!string.IsNullOrWhiteSpace(lead.LegalForm))
        {
            var legalForm = lead.LegalForm.ToLowerInvariant();
            if (legalForm.Contains("eenmanszaak") || legalForm.Contains("zelfstandig"))
                return true;
        }

        if (!string.IsNullOrWhiteSpace(lead.EmployeeCount))
        {
            var employeeStr = lead.EmployeeCount.ToLowerInvariant().Replace(" ", "");
            if (int.TryParse(employeeStr, out var count) && count <= 5)
                return true;
            if (employeeStr.Contains("-"))
            {
                var parts = employeeStr.Split('-');
                if (parts.Length == 2 && int.TryParse(parts[1], out var maxCount) && maxCount <= 5)
                    return true;
            }
        }

        return false;
    }
}
