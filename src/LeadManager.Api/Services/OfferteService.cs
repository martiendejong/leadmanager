using LeadManager.Api.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;

namespace LeadManager.Api.Services;

public class OfferteLineItem
{
    public string Description { get; set; } = "";
    public string? SubDescription { get; set; }
    public decimal Hours { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Total => Hours * UnitPrice;
}

public class OfferteData
{
    public string QuoteNumber { get; set; } = "";
    public DateTime Date { get; set; } = DateTime.Today;
    public DateTime ValidUntil { get; set; }
    public Client Client { get; set; } = null!;
    public Lead? Lead { get; set; }
    public List<OfferteLineItem> LineItems { get; set; } = new();
    public SalesSettings Settings { get; set; } = null!;
    public string? CustomNote { get; set; }

    public decimal Subtotal => LineItems.Sum(i => i.Total);
    public decimal VatAmount => Subtotal * 0.21m;
    public decimal TotalIncVat => Subtotal + VatAmount;
}

public class OfferteService
{
    private readonly ILogger<OfferteService> _logger;

    public OfferteService(ILogger<OfferteService> logger)
    {
        _logger = logger;
    }

    public byte[] Generate(OfferteData data)
    {
        try
        {
            return GeneratePdf(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PDF generation failed for quote {Number}", data.QuoteNumber);
            throw;
        }
    }

    private byte[] GeneratePdf(OfferteData data)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);

        // Fonts
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);

        double w = page.PageSize.Width;
        double h = page.PageSize.Height;
        double margin = 50;
        double y = h - margin;

        // ── Header ────────────────────────────────────────────────────────
        // Company name (large, bold)
        page.SetTextAndFillColor(30, 64, 175); // indigo-800
        page.AddText(data.Settings.CompanyName, 22, new PdfPoint(margin, y - 22), bold);

        // Company details (right side, small)
        page.SetTextAndFillColor(80, 80, 80);
        double rx = w - margin;
        var companyLines = new List<string>();
        if (!string.IsNullOrEmpty(data.Settings.CompanyAddress)) companyLines.Add(data.Settings.CompanyAddress);
        if (!string.IsNullOrEmpty(data.Settings.CompanyZipCode) || !string.IsNullOrEmpty(data.Settings.CompanyCity))
            companyLines.Add($"{data.Settings.CompanyZipCode} {data.Settings.CompanyCity}".Trim());
        if (!string.IsNullOrEmpty(data.Settings.CompanyEmail)) companyLines.Add(data.Settings.CompanyEmail);
        if (!string.IsNullOrEmpty(data.Settings.CompanyPhone)) companyLines.Add(data.Settings.CompanyPhone);
        if (!string.IsNullOrEmpty(data.Settings.CompanyKvk)) companyLines.Add($"KvK: {data.Settings.CompanyKvk}");
        if (!string.IsNullOrEmpty(data.Settings.CompanyVat)) companyLines.Add($"BTW: {data.Settings.CompanyVat}");

        double cy = y - 14;
        foreach (var line in companyLines)
        {
            var lineWidth = EstimateTextWidth(line, 8);
            page.AddText(line, 8, new PdfPoint(rx - lineWidth, cy), regular);
            cy -= 12;
        }

        y -= 50;

        // Divider
        page.SetTextAndFillColor(0, 0, 0);
        DrawLine(page, margin, y, w - margin, y);
        y -= 20;

        // ── Title + Meta ──────────────────────────────────────────────────
        page.SetTextAndFillColor(30, 64, 175);
        page.AddText("OFFERTE", 16, new PdfPoint(margin, y), bold);

        page.SetTextAndFillColor(80, 80, 80);
        var metaX = w - margin - 180;
        page.AddText($"Nummer: {data.QuoteNumber}", 9, new PdfPoint(metaX, y), regular);
        page.AddText($"Datum: {data.Date:d MMMM yyyy}", 9, new PdfPoint(metaX, y - 14), regular);
        page.AddText($"Geldig tot: {data.ValidUntil:d MMMM yyyy}", 9, new PdfPoint(metaX, y - 28), regular);
        y -= 50;

        // ── Client block ──────────────────────────────────────────────────
        page.SetTextAndFillColor(0, 0, 0);
        page.AddText("Aan:", 9, new PdfPoint(margin, y), regular);
        y -= 14;
        page.SetTextAndFillColor(30, 30, 30);
        page.AddText(data.Client.Name, 10, new PdfPoint(margin, y), bold);
        y -= 13;
        if (!string.IsNullOrEmpty(data.Client.PrimaryContactName))
        {
            page.AddText($"t.a.v. {data.Client.PrimaryContactName}", 9, new PdfPoint(margin, y), regular);
            y -= 12;
        }
        if (!string.IsNullOrEmpty(data.Client.City))
        {
            page.AddText(data.Client.City, 9, new PdfPoint(margin, y), regular);
            y -= 12;
        }
        y -= 20;

        // ── Intro text ────────────────────────────────────────────────────
        var intro = data.Settings.QuoteIntroText ??
            $"Hierbij bieden wij u vrijblijvend de volgende offerte aan voor onze dienstverlening.";
        page.SetTextAndFillColor(50, 50, 50);
        WrapText(page, intro, 9, margin, y, w - 2 * margin, regular, ref y, 13);
        y -= 20;

        // ── Custom note ───────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(data.CustomNote))
        {
            page.SetTextAndFillColor(70, 70, 70);
            WrapText(page, data.CustomNote, 9, margin, y, w - 2 * margin, regular, ref y, 13);
            y -= 16;
        }

        // ── Line items table ──────────────────────────────────────────────
        double col1 = margin;
        double col2 = w - margin - 200;
        double col3 = w - margin - 110;
        double col4 = w - margin - 60;
        double col5 = w - margin;

        // Table header
        page.SetTextAndFillColor(255, 255, 255);
        FillRect(page, margin, y - 2, w - 2 * margin, 18, 30, 64, 175);
        page.AddText("Omschrijving", 8.5, new PdfPoint(col1 + 4, y + 4), bold);
        page.AddText("Uren", 8.5, new PdfPoint(col2 + 4, y + 4), bold);
        page.AddText("Prijs/u", 8.5, new PdfPoint(col3 + 4, y + 4), bold);
        page.AddText("Totaal", 8.5, new PdfPoint(col4 + 4, y + 4), bold);
        y -= 22;

        // Rows
        bool shade = false;
        foreach (var item in data.LineItems)
        {
            if (shade) FillRect(page, margin, y - 2, w - 2 * margin, 16, 245, 247, 252);
            page.SetTextAndFillColor(20, 20, 20);
            page.AddText(item.Description, 9, new PdfPoint(col1 + 4, y + 2), bold);
            page.SetTextAndFillColor(60, 60, 60);
            page.AddText($"{item.Hours:0.#}", 9, new PdfPoint(col2 + 4, y + 2), regular);
            page.AddText($"€ {item.UnitPrice:0.00}", 9, new PdfPoint(col3 + 4, y + 2), regular);
            page.AddText($"€ {item.Total:0.00}", 9, new PdfPoint(col4 + 4, y + 2), regular);
            y -= 16;

            if (!string.IsNullOrEmpty(item.SubDescription))
            {
                page.SetTextAndFillColor(100, 100, 100);
                page.AddText(item.SubDescription, 8, new PdfPoint(col1 + 4, y + 2), regular);
                y -= 13;
            }

            shade = !shade;
        }

        y -= 8;
        DrawLine(page, margin, y, w - margin, y);
        y -= 16;

        // Totals
        double totX = w - margin - 160;
        page.SetTextAndFillColor(60, 60, 60);
        page.AddText("Subtotaal (excl. BTW):", 9, new PdfPoint(totX, y), regular);
        page.AddText($"€ {data.Subtotal:0.00}", 9, new PdfPoint(w - margin - 5 - EstimateTextWidth($"€ {data.Subtotal:0.00}", 9), y), regular);
        y -= 14;
        page.AddText("BTW (21%):", 9, new PdfPoint(totX, y), regular);
        page.AddText($"€ {data.VatAmount:0.00}", 9, new PdfPoint(w - margin - 5 - EstimateTextWidth($"€ {data.VatAmount:0.00}", 9), y), regular);
        y -= 16;

        FillRect(page, totX - 4, y - 2, w - margin - totX + 4, 18, 30, 64, 175);
        page.SetTextAndFillColor(255, 255, 255);
        page.AddText("TOTAAL incl. BTW:", 9, new PdfPoint(totX, y + 4), bold);
        page.AddText($"€ {data.TotalIncVat:0.00}", 9, new PdfPoint(w - margin - 5 - EstimateTextWidth($"€ {data.TotalIncVat:0.00}", 9), y + 4), bold);
        y -= 30;

        // ── Payment / Terms ───────────────────────────────────────────────
        var terms = data.Settings.QuoteTerms ??
            $"Betaling binnen 14 dagen na factuurdatum. IBAN: {data.Settings.CompanyIban ?? "—"}.";
        y -= 10;
        DrawLine(page, margin, y, w - margin, y);
        y -= 16;
        page.SetTextAndFillColor(80, 80, 80);
        page.AddText("Voorwaarden", 9, new PdfPoint(margin, y), bold);
        y -= 13;
        WrapText(page, terms, 8.5, margin, y, w - 2 * margin, regular, ref y, 12);

        // ── Footer ────────────────────────────────────────────────────────
        var footer = data.Settings.QuoteFooterText ?? $"Met vriendelijke groet, {data.Settings.CompanyName}";
        page.SetTextAndFillColor(120, 120, 120);
        page.AddText(footer, 8, new PdfPoint(margin, 40), regular);
        if (!string.IsNullOrEmpty(data.Settings.CompanyWebsite))
            page.AddText(data.Settings.CompanyWebsite, 8, new PdfPoint(w - margin - EstimateTextWidth(data.Settings.CompanyWebsite, 8), 40), regular);

        return builder.Build();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static void DrawLine(PdfPageBuilder page, double x1, double y, double x2, double y2)
    {
        page.SetTextAndFillColor(200, 200, 200);
        // PdfPig: draw thin rect as line
        page.DrawRectangle(new PdfPoint(x1, y), x2 - x1, 0.5, 0.5);
    }

    private static void FillRect(PdfPageBuilder page, double x, double y, double w, double h,
        byte r, byte g, byte b)
    {
        page.SetTextAndFillColor(r, g, b);
        page.DrawRectangle(new PdfPoint(x, y), w, h);
    }

    private static void WrapText(PdfPageBuilder page, string text, double fontSize,
        double x, double startY, double maxWidth, PdfDocumentBuilder.AddedFont font,
        ref double y, double lineHeight)
    {
        var words = text.Split(' ');
        var line = "";
        foreach (var word in words)
        {
            var test = line.Length == 0 ? word : line + " " + word;
            if (EstimateTextWidth(test, fontSize) > maxWidth && line.Length > 0)
            {
                page.AddText(line, fontSize, new PdfPoint(x, y), font);
                y -= lineHeight;
                line = word;
            }
            else line = test;
        }
        if (line.Length > 0)
        {
            page.AddText(line, fontSize, new PdfPoint(x, y), font);
            y -= lineHeight;
        }
    }

    private static double EstimateTextWidth(string text, double fontSize) =>
        text.Length * fontSize * 0.52;
}
