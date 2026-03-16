using LeadManager.Api.Models;

namespace LeadManager.Api.Services.Enrichment;

public class SalesScoreService
{
    public int CalculateScore(Lead lead)
    {
        var score = 0;

        // LinkedIn URL present: +2 points
        if (!string.IsNullOrWhiteSpace(lead.LinkedInUrl))
            score += 2;

        // Mobile phone (06) present: +2 points
        if (!string.IsNullOrWhiteSpace(lead.Phone) && lead.Phone.Contains("06"))
            score += 2;

        // Owner name known: +1 point
        if (!string.IsNullOrWhiteSpace(lead.OwnerName))
            score += 1;

        // High Google rating: +1 point
        if (lead.GoogleRating >= 4.5f)
            score += 1;

        // Small company (solo or <= 5 employees): +1 point
        if (IsSmallCompany(lead))
            score += 1;

        // Established company (founded before or in 2015): +1 point
        if (lead.FoundingYear.HasValue && lead.FoundingYear.Value <= 2015)
            score += 1;

        // Personal email known: +1 point
        if (!string.IsNullOrWhiteSpace(lead.PersonalEmail))
            score += 1;

        // Unreachable website: -1 point
        if (lead.WebsiteStatus == WebsiteStatus.Unreachable)
            score -= 1;

        // Ensure score is between 0-10
        return Math.Max(0, Math.Min(10, score));
    }

    private static bool IsSmallCompany(Lead lead)
    {
        // Check if it's a sole proprietorship
        if (!string.IsNullOrWhiteSpace(lead.LegalForm))
        {
            var legalForm = lead.LegalForm.ToLowerInvariant();
            if (legalForm.Contains("eenmanszaak") || legalForm.Contains("zelfstandig"))
                return true;
        }

        // Check employee count
        if (!string.IsNullOrWhiteSpace(lead.EmployeeCount))
        {
            // Try to parse employee count (handle formats like "1-5", "<=5", "5")
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
