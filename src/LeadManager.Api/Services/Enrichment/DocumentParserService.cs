using System.Text;
using UglyToad.PdfPig;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LeadManager.Api.Models;
using LeadManager.Api.Data;

namespace LeadManager.Api.Services.Enrichment;

public class DocumentParserService
{
    private readonly ILogger<DocumentParserService> _logger;

    public DocumentParserService(ILogger<DocumentParserService> logger)
    {
        _logger = logger;
    }

    public async Task<List<string>> ParseDocumentsAsync(
        LeadManagerDbContext db,
        Guid leadId,
        List<IFormFile> files)
    {
        var parsedTexts = new List<string>();

        foreach (var file in files)
        {
            try
            {
                var text = await ParseSingleFileAsync(file);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parsedTexts.Add(text);

                    // Store as LeadPageContent with Source="document"
                    var content = new LeadPageContent
                    {
                        LeadId = leadId,
                        Url = $"document://{file.FileName}",
                        RawText = text,
                        Source = "document",
                        HttpStatus = 200
                    };
                    db.LeadPageContents.Add(content);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse document: {FileName}", file.FileName);
            }
        }

        if (parsedTexts.Count > 0)
        {
            await db.SaveChangesAsync();
        }

        return parsedTexts;
    }

    private async Task<string> ParseSingleFileAsync(IFormFile file)
    {
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

        return extension switch
        {
            ".pdf" => await ParsePdfAsync(file),
            ".docx" => await ParseDocxAsync(file),
            ".txt" => await ParseTxtAsync(file),
            _ => throw new NotSupportedException($"File type {extension} is not supported")
        };
    }

    private async Task<string> ParsePdfAsync(IFormFile file)
    {
        using var stream = file.OpenReadStream();
        using var pdf = PdfDocument.Open(stream);

        var text = new StringBuilder();
        foreach (var page in pdf.GetPages())
        {
            text.AppendLine(page.Text);
        }

        return text.ToString();
    }

    private async Task<string> ParseDocxAsync(IFormFile file)
    {
        using var stream = file.OpenReadStream();
        using var doc = WordprocessingDocument.Open(stream, false);

        var body = doc.MainDocumentPart?.Document?.Body;
        if (body == null) return "";

        var text = new StringBuilder();
        foreach (var paragraph in body.Elements<Paragraph>())
        {
            text.AppendLine(paragraph.InnerText);
        }

        return text.ToString();
    }

    private async Task<string> ParseTxtAsync(IFormFile file)
    {
        using var reader = new StreamReader(file.OpenReadStream());
        return await reader.ReadToEndAsync();
    }

    public static bool IsValidFileType(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension is ".pdf" or ".docx" or ".txt";
    }

    public static bool IsValidFileSize(long fileSize, long maxSizeBytes = 10 * 1024 * 1024)
    {
        return fileSize > 0 && fileSize <= maxSizeBytes;
    }
}
