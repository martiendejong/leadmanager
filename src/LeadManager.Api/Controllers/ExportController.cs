using LeadManager.Api.Data;
using LeadManager.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace LeadManager.Api.Controllers;

[Route("api/export")]
[ApiController]
[Authorize]
public class ExportController : ControllerBase
{
    private readonly LeadManagerDbContext _db;

    public ExportController(LeadManagerDbContext db)
    {
        _db = db;
    }

    // GET /api/export/leads?ids=guid1,guid2,guid3
    [HttpGet("leads")]
    public async Task<IActionResult> ExportLeads([FromQuery] string ids)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        var guidList = ids.Split(',')
            .Select(id => Guid.TryParse(id.Trim(), out var guid) ? guid : Guid.Empty)
            .Where(guid => guid != Guid.Empty)
            .ToList();

        if (!guidList.Any())
        {
            return BadRequest("No valid lead IDs provided");
        }

        var leads = await _db.Leads
            .Where(l => l.ImportedByUserId == userId && guidList.Contains(l.Id))
            .OrderByDescending(l => l.SalesPriorityScore ?? 0)
            .ThenBy(l => l.Name)
            .ToListAsync();

        if (!leads.Any())
        {
            return NotFound("No leads found with provided IDs");
        }

        var html = GenerateHtmlReport(leads);
        return Content(html, "text/html", Encoding.UTF8);
    }

    private string GenerateHtmlReport(List<Lead> leads)
    {
        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"nl\">");
        sb.AppendLine("<head>");
        sb.AppendLine("    <meta charset=\"UTF-8\">");
        sb.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("    <title>Leads Export - LeadManager</title>");
        sb.AppendLine("    <style>");
        sb.AppendLine("        @media print {");
        sb.AppendLine("            .no-print { display: none; }");
        sb.AppendLine("            .page-break { page-break-after: always; }");
        sb.AppendLine("        }");
        sb.AppendLine("        * { margin: 0; padding: 0; box-sizing: border-box; }");
        sb.AppendLine("        body { font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; line-height: 1.6; color: #333; background: #f5f5f5; padding: 20px; }");
        sb.AppendLine("        .container { max-width: 1200px; margin: 0 auto; background: white; padding: 40px; box-shadow: 0 2px 8px rgba(0,0,0,0.1); }");
        sb.AppendLine("        .header { border-bottom: 3px solid #4f46e5; padding-bottom: 20px; margin-bottom: 30px; }");
        sb.AppendLine("        .header h1 { color: #4f46e5; font-size: 28px; font-weight: 600; }");
        sb.AppendLine("        .header .meta { color: #666; font-size: 14px; margin-top: 5px; }");
        sb.AppendLine("        .lead-card { border: 1px solid #e5e7eb; border-radius: 8px; padding: 20px; margin-bottom: 20px; background: #fafafa; }");
        sb.AppendLine("        .lead-card.high-priority { border-left: 4px solid #10b981; }");
        sb.AppendLine("        .lead-card.medium-priority { border-left: 4px solid #f59e0b; }");
        sb.AppendLine("        .lead-card.low-priority { border-left: 4px solid #ef4444; }");
        sb.AppendLine("        .lead-header { display: flex; justify-content: space-between; align-items: flex-start; margin-bottom: 15px; }");
        sb.AppendLine("        .lead-header h2 { color: #111827; font-size: 20px; font-weight: 600; }");
        sb.AppendLine("        .lead-header .badge { padding: 4px 12px; border-radius: 12px; font-size: 12px; font-weight: 600; }");
        sb.AppendLine("        .badge.high { background: #d1fae5; color: #065f46; }");
        sb.AppendLine("        .badge.medium { background: #fef3c7; color: #92400e; }");
        sb.AppendLine("        .badge.low { background: #fee2e2; color: #991b1b; }");
        sb.AppendLine("        .lead-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(250px, 1fr)); gap: 15px; margin-top: 15px; }");
        sb.AppendLine("        .field { }");
        sb.AppendLine("        .field-label { font-size: 11px; text-transform: uppercase; color: #6b7280; font-weight: 600; letter-spacing: 0.5px; }");
        sb.AppendLine("        .field-value { font-size: 14px; color: #111827; margin-top: 2px; word-break: break-word; }");
        sb.AppendLine("        .field-value a { color: #4f46e5; text-decoration: none; }");
        sb.AppendLine("        .field-value a:hover { text-decoration: underline; }");
        sb.AppendLine("        .section-title { font-size: 14px; font-weight: 600; color: #4f46e5; margin-top: 15px; margin-bottom: 10px; border-bottom: 1px solid #e5e7eb; padding-bottom: 5px; }");
        sb.AppendLine("        .sales-approach { background: #f3f4f6; padding: 15px; border-radius: 6px; margin-top: 15px; }");
        sb.AppendLine("        .sales-approach h4 { font-size: 12px; color: #6b7280; margin-bottom: 5px; }");
        sb.AppendLine("        .sales-approach p { font-size: 13px; color: #374151; white-space: pre-wrap; }");
        sb.AppendLine("        .print-button { position: fixed; top: 20px; right: 20px; background: #4f46e5; color: white; padding: 12px 24px; border: none; border-radius: 6px; cursor: pointer; font-weight: 600; box-shadow: 0 4px 6px rgba(0,0,0,0.1); }");
        sb.AppendLine("        .print-button:hover { background: #4338ca; }");
        sb.AppendLine("        .summary { background: #eff6ff; border: 1px solid #bfdbfe; border-radius: 6px; padding: 15px; margin-bottom: 30px; }");
        sb.AppendLine("        .summary-row { display: flex; justify-content: space-between; align-items: center; }");
        sb.AppendLine("        .summary-row span { font-size: 14px; color: #1e40af; }");
        sb.AppendLine("        .summary-row strong { font-size: 24px; color: #1e3a8a; }");
        sb.AppendLine("    </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("    <button class=\"print-button no-print\" onclick=\"window.print()\">🖨️ Print / Save as PDF</button>");
        sb.AppendLine("    <div class=\"container\">");
        sb.AppendLine("        <div class=\"header\">");
        sb.AppendLine("            <h1>Leads Export</h1>");
        sb.AppendLine($"            <div class=\"meta\">Gegenereerd op {DateTime.Now:dd-MM-yyyy HH:mm} • {leads.Count} lead(s)</div>");
        sb.AppendLine("        </div>");

        // Summary
        var highPriority = leads.Count(l => (l.SalesPriorityScore ?? 0) >= 7);
        var mediumPriority = leads.Count(l => (l.SalesPriorityScore ?? 0) >= 4 && (l.SalesPriorityScore ?? 0) < 7);
        var lowPriority = leads.Count(l => (l.SalesPriorityScore ?? 0) < 4);

        sb.AppendLine("        <div class=\"summary\">");
        sb.AppendLine("            <div class=\"summary-row\">");
        sb.AppendLine($"                <span>Hoge prioriteit:</span><strong>{highPriority}</strong>");
        sb.AppendLine("            </div>");
        sb.AppendLine("            <div class=\"summary-row\">");
        sb.AppendLine($"                <span>Normale prioriteit:</span><strong>{mediumPriority}</strong>");
        sb.AppendLine("            </div>");
        sb.AppendLine("            <div class=\"summary-row\">");
        sb.AppendLine($"                <span>Lage prioriteit:</span><strong>{lowPriority}</strong>");
        sb.AppendLine("            </div>");
        sb.AppendLine("        </div>");

        // Lead cards
        foreach (var lead in leads)
        {
            var score = lead.SalesPriorityScore ?? 0;
            var priorityClass = score >= 7 ? "high-priority" : score >= 4 ? "medium-priority" : "low-priority";
            var badgeClass = score >= 7 ? "high" : score >= 4 ? "medium" : "low";

            sb.AppendLine($"        <div class=\"lead-card {priorityClass}\">");
            sb.AppendLine("            <div class=\"lead-header\">");
            sb.AppendLine($"                <h2>{Escape(lead.Name)}</h2>");
            sb.AppendLine($"                <span class=\"badge {badgeClass}\">Score: {score}/10</span>");
            sb.AppendLine("            </div>");

            sb.AppendLine("            <div class=\"lead-grid\">");

            // Basic info
            if (!string.IsNullOrWhiteSpace(lead.Sector))
                AppendField(sb, "Sector", lead.Sector);
            if (!string.IsNullOrWhiteSpace(lead.City))
                AppendField(sb, "Stad", lead.City);
            if (!string.IsNullOrWhiteSpace(lead.Website))
                AppendField(sb, "Website", lead.Website, isUrl: true);

            // Contact
            if (!string.IsNullOrWhiteSpace(lead.OwnerName))
                AppendField(sb, "Eigenaar", lead.OwnerName);
            if (!string.IsNullOrWhiteSpace(lead.Phone))
                AppendField(sb, "Telefoon", lead.Phone);
            if (!string.IsNullOrWhiteSpace(lead.PersonalEmail))
                AppendField(sb, "Persoonlijk e-mail", lead.PersonalEmail);
            if (!string.IsNullOrWhiteSpace(lead.CompanyEmail))
                AppendField(sb, "Zakelijk e-mail", lead.CompanyEmail);
            if (!string.IsNullOrWhiteSpace(lead.LinkedInUrl))
                AppendField(sb, "LinkedIn", lead.LinkedInUrl, isUrl: true);

            sb.AppendLine("            </div>");

            // KvK Data
            if (!string.IsNullOrWhiteSpace(lead.KvkNumber) || lead.FoundingYear.HasValue)
            {
                sb.AppendLine("            <div class=\"section-title\">KvK Gegevens</div>");
                sb.AppendLine("            <div class=\"lead-grid\">");
                if (!string.IsNullOrWhiteSpace(lead.KvkNumber))
                    AppendField(sb, "KvK nummer", lead.KvkNumber);
                if (!string.IsNullOrWhiteSpace(lead.EmployeeCount))
                    AppendField(sb, "Werknemers", lead.EmployeeCount);
                if (lead.FoundingYear.HasValue)
                    AppendField(sb, "Opgericht", lead.FoundingYear.Value.ToString());
                if (!string.IsNullOrWhiteSpace(lead.LegalForm))
                    AppendField(sb, "Rechtsvorm", lead.LegalForm);
                sb.AppendLine("            </div>");
            }

            // Google Rating
            if (lead.GoogleRating.HasValue)
            {
                sb.AppendLine("            <div class=\"section-title\">Google Reviews</div>");
                sb.AppendLine("            <div class=\"lead-grid\">");
                AppendField(sb, "Beoordeling", $"⭐ {lead.GoogleRating:F1} ({lead.GoogleReviewCount} reviews)");
                sb.AppendLine("            </div>");
            }

            // Extended contact info
            if (!string.IsNullOrWhiteSpace(lead.OwnerLinkedInUrl) || !string.IsNullOrWhiteSpace(lead.OwnerMobile) || !string.IsNullOrWhiteSpace(lead.InternalContactName))
            {
                sb.AppendLine("            <div class=\"section-title\">Eigenaar & Contact</div>");
                sb.AppendLine("            <div class=\"lead-grid\">");
                if (!string.IsNullOrWhiteSpace(lead.OwnerLinkedInUrl))
                    AppendField(sb, "LinkedIn eigenaar", lead.OwnerLinkedInUrl, isUrl: true);
                if (!string.IsNullOrWhiteSpace(lead.OwnerMobile))
                    AppendField(sb, "Mobiel eigenaar", lead.OwnerMobile);
                if (!string.IsNullOrWhiteSpace(lead.InternalContactName))
                    AppendField(sb, "Interne contactpersoon", lead.InternalContactName + (!string.IsNullOrWhiteSpace(lead.InternalContactRole) ? $" ({lead.InternalContactRole})" : ""));
                sb.AppendLine("            </div>");
            }

            // Operational info
            if (!string.IsNullOrWhiteSpace(lead.WorkingArea) || !string.IsNullOrWhiteSpace(lead.Certifications) || !string.IsNullOrWhiteSpace(lead.PricingInfo) || !string.IsNullOrWhiteSpace(lead.OpeningHours))
            {
                sb.AppendLine("            <div class=\"section-title\">Bedrijfsinformatie</div>");
                sb.AppendLine("            <div class=\"lead-grid\">");
                if (!string.IsNullOrWhiteSpace(lead.WorkingArea))
                    AppendField(sb, "Werkgebied", lead.WorkingArea);
                if (!string.IsNullOrWhiteSpace(lead.Certifications))
                    AppendField(sb, "Certificeringen", lead.Certifications);
                if (!string.IsNullOrWhiteSpace(lead.PricingInfo))
                    AppendField(sb, "Prijsinformatie", lead.PricingInfo);
                if (!string.IsNullOrWhiteSpace(lead.OpeningHours))
                    AppendField(sb, "Openingstijden", lead.OpeningHours);
                sb.AppendLine("            </div>");
            }

            // Priority reasoning
            if (!string.IsNullOrWhiteSpace(lead.SalesPriorityLabel) || !string.IsNullOrWhiteSpace(lead.SalesPriorityReasoning))
            {
                sb.AppendLine("            <div class=\"section-title\">Sales Prioriteit</div>");
                sb.AppendLine("            <div class=\"lead-grid\">");
                if (!string.IsNullOrWhiteSpace(lead.SalesPriorityLabel))
                    AppendField(sb, "Label", lead.SalesPriorityLabel);
                if (!string.IsNullOrWhiteSpace(lead.SalesPriorityReasoning))
                    AppendField(sb, "Onderbouwing", lead.SalesPriorityReasoning);
                sb.AppendLine("            </div>");
            }

            // Signals
            if (!string.IsNullOrWhiteSpace(lead.Signals))
            {
                try
                {
                    var signals = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string>>>(lead.Signals);
                    if (signals != null && signals.Count > 0)
                    {
                        sb.AppendLine("            <div class=\"section-title\">Signalen</div>");
                        sb.AppendLine("            <div class=\"sales-approach\">");
                        foreach (var signal in signals)
                        {
                            if (signal.TryGetValue("message", out var msg))
                            {
                                var severity = signal.GetValueOrDefault("severity", "info");
                                var icon = severity == "alert" ? "🔴" : severity == "warning" ? "🟡" : "🔵";
                                sb.AppendLine($"                <p>{icon} {Escape(msg)}</p>");
                            }
                        }
                        sb.AppendLine("            </div>");
                    }
                }
                catch
                {
                    // Ignore JSON parsing errors
                }
            }

            // Sales Approach
            if (!string.IsNullOrWhiteSpace(lead.SalesApproach))
            {
                try
                {
                    var approach = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(lead.SalesApproach);
                    if (approach != null && approach.Any())
                    {
                        sb.AppendLine("            <div class=\"section-title\">AI Sales Approach</div>");
                        sb.AppendLine("            <div class=\"sales-approach\">");
                        if (approach.ContainsKey("linkedinMessage"))
                        {
                            sb.AppendLine("                <h4>LinkedIn bericht:</h4>");
                            sb.AppendLine($"                <p>{Escape(approach["linkedinMessage"])}</p>");
                        }
                        if (approach.ContainsKey("phoneOpener"))
                        {
                            sb.AppendLine("                <h4>Telefoon opener:</h4>");
                            sb.AppendLine($"                <p>{Escape(approach["phoneOpener"])}</p>");
                        }
                        if (approach.ContainsKey("emailIntro"))
                        {
                            sb.AppendLine("                <h4>Email intro:</h4>");
                            sb.AppendLine($"                <p>{Escape(approach["emailIntro"])}</p>");
                        }
                        sb.AppendLine("            </div>");
                    }
                }
                catch
                {
                    // Ignore JSON parsing errors
                }
            }

            sb.AppendLine("        </div>");
        }

        sb.AppendLine("    </div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    private void AppendField(StringBuilder sb, string label, string value, bool isUrl = false)
    {
        sb.AppendLine("                <div class=\"field\">");
        sb.AppendLine($"                    <div class=\"field-label\">{Escape(label)}</div>");
        if (isUrl && !string.IsNullOrWhiteSpace(value))
        {
            var url = value.StartsWith("http") ? value : $"https://{value}";
            sb.AppendLine($"                    <div class=\"field-value\"><a href=\"{Escape(url)}\" target=\"_blank\">{Escape(value)}</a></div>");
        }
        else
        {
            sb.AppendLine($"                    <div class=\"field-value\">{Escape(value)}</div>");
        }
        sb.AppendLine("                </div>");
    }

    private static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "—";
        return System.Net.WebUtility.HtmlEncode(text);
    }
}
