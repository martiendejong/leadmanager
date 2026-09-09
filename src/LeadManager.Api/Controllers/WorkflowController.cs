using LeadManager.Api.Data;
using LeadManager.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeadManager.Api.Controllers;

[Route("api/leads/{id:guid}")]
[ApiController]
[Authorize]
public class WorkflowController : ControllerBase
{
    private readonly LeadManagerDbContext _db;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<WorkflowController> _logger;

    public WorkflowController(
        LeadManagerDbContext db,
        IConfiguration config,
        IHttpClientFactory httpFactory,
        ILogger<WorkflowController> logger)
    {
        _db = db;
        _config = config;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    private string? UserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    // POST /api/leads/{id}/intake
    [HttpPost("intake")]
    public async Task<IActionResult> RunIntake(Guid id)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.ImportedByUserId == UserId);
        if (lead == null) return NotFound();

        var context = BuildLeadContext(lead);
        var apiKey = _config["OpenAI:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
            return BadRequest("OpenAI niet geconfigureerd");

        var systemPrompt =
            "Je bent een senior business analyst en software architect. " +
            "Analyseer de verstrekte bedrijfsinformatie en stel een diepgaande intake op. " +
            "Geef een JSON-object terug met de velden: " +
            "pijnpunten (array van strings), " +
            "kansen (array van strings), " +
            "applicatieType (string: bijv. 'AI Medewerker', 'Slimme Website', 'Automatiseringssysteem', 'Data Dashboard'), " +
            "verkoopargument (string: 1 concrete zin), " +
            "techStack (array van strings), " +
            "complexiteit (string: 'laag', 'middel', 'hoog'), " +
            "schattingWeken (number). " +
            "Antwoord ALLEEN met geldige JSON, geen markdown.";

        var userPrompt = $"Bedrijfsinformatie:\n{context}\n\nGeef de intake-analyse als JSON.";

        try
        {
            var result = await CallOpenAiAsync(apiKey, systemPrompt, userPrompt, "gpt-4o-mini", 0.3);

            // Validate it's JSON
            JsonDocument.Parse(result);

            // Persist in workflowData
            var data = ParseWorkflowData(lead.WorkflowDataJson);
            data["intake"] = JsonNode.Parse(result)!;
            lead.WorkflowDataJson = data.ToJsonString();
            lead.WorkflowStep = Math.Max(lead.WorkflowStep, 3);
            await _db.SaveChangesAsync();

            return Ok(new { intake = JsonDocument.Parse(result).RootElement, workflowDataJson = lead.WorkflowDataJson });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Intake generation failed for lead {Id}", id);
            return StatusCode(500, "Intake kon niet worden gegenereerd");
        }
    }

    // POST /api/leads/{id}/proposal
    [HttpPost("proposal")]
    public async Task<IActionResult> RunProposal(Guid id)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.ImportedByUserId == UserId);
        if (lead == null) return NotFound();

        var context = BuildLeadContext(lead);
        var intakeJson = "";
        if (!string.IsNullOrEmpty(lead.WorkflowDataJson))
        {
            var data = ParseWorkflowData(lead.WorkflowDataJson);
            if (data["intake"] is JsonNode intakeNode)
                intakeJson = $"\n\nIntake-analyse:\n{intakeNode.ToJsonString()}";
        }

        var apiKey = _config["OpenAI:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
            return BadRequest("OpenAI niet geconfigureerd");

        var systemPrompt =
            "Je bent een software architect bij een AI-bureau. " +
            "Schrijf een concreet applicatievoorstel voor dit bedrijf. " +
            "Geef een JSON-object terug met de velden: " +
            "titel (string: naam van de applicatie), " +
            "samenvatting (string: 2-3 zinnen), " +
            "functionaliteiten (array van strings, max 8), " +
            "meerwaarde (string: kwantificeerbaar voordeel), " +
            "technologie (string: bijv. '.NET + React + AI'), " +
            "fasering (array van objecten: {fase: string, omschrijving: string}), " +
            "prijs (string: bijv. '€4.500 - €8.000 eenmalig'). " +
            "Antwoord ALLEEN met geldige JSON, geen markdown.";

        var userPrompt = $"Bedrijfsinformatie:\n{context}{intakeJson}\n\nSchrijf een applicatievoorstel als JSON.";

        try
        {
            var result = await CallOpenAiAsync(apiKey, systemPrompt, userPrompt, "gpt-4o-mini", 0.4);

            JsonDocument.Parse(result);

            var data = ParseWorkflowData(lead.WorkflowDataJson);
            data["proposal"] = JsonNode.Parse(result)!;
            lead.WorkflowDataJson = data.ToJsonString();
            lead.WorkflowStep = Math.Max(lead.WorkflowStep, 4);
            await _db.SaveChangesAsync();

            return Ok(new { proposal = JsonDocument.Parse(result).RootElement, workflowDataJson = lead.WorkflowDataJson });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Proposal generation failed for lead {Id}", id);
            return StatusCode(500, "Voorstel kon niet worden gegenereerd");
        }
    }

    // POST /api/leads/{id}/poc
    [HttpPost("poc")]
    public async Task<IActionResult> CreatePoc(Guid id)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.ImportedByUserId == UserId);
        if (lead == null) return NotFound();

        var tasksBaseUrl = _config["JengoWork:BaseUrl"] ?? "https://tasks.prospergenics.com";
        var tasksApiKey = _config["JengoWork:ApiKey"];
        if (string.IsNullOrEmpty(tasksApiKey))
            return BadRequest("JengoWork API niet geconfigureerd");

        // Get proposal from workflowData if available
        string boardName = $"PoC — {lead.Name}";
        string boardDescription = $"Proof of Concept voor {lead.Name} ({lead.Sector}, {lead.City})";
        var initialTasks = new List<string>
        {
            $"Intake & requirements voor {lead.Name}",
            "Technische architectuur opstellen",
            "MVP bouwen (basisfunctionaliteiten)",
            "Demo voorbereiden",
            "Feedback verwerken & verfijnen",
            "PoC opleveren aan klant"
        };

        if (!string.IsNullOrEmpty(lead.WorkflowDataJson))
        {
            var wfData = ParseWorkflowData(lead.WorkflowDataJson);
            if (wfData["proposal"] is JsonNode proposalNode)
            {
                try
                {
                    var prop = JsonDocument.Parse(proposalNode.ToJsonString()).RootElement;
                    if (prop.TryGetProperty("titel", out var t)) boardName = $"PoC — {t.GetString()}";
                    if (prop.TryGetProperty("samenvatting", out var s)) boardDescription = s.GetString() ?? boardDescription;
                    if (prop.TryGetProperty("functionaliteiten", out var f) && f.ValueKind == JsonValueKind.Array)
                    {
                        initialTasks = f.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList();
                        initialTasks.Insert(0, "Intake & requirements");
                        initialTasks.Add("Demo voorbereiden & opleveren");
                    }
                }
                catch { }
            }
        }

        var tasksHttp = _httpFactory.CreateClient();
        tasksHttp.DefaultRequestHeaders.Add("Authorization", tasksApiKey);

        var githubToken = _config["GitHub:Token"];
        var githubOwner = _config["GitHub:Owner"] ?? "martiendejong";

        try
        {
            // 1. Create Tasks board
            var boardResp = await tasksHttp.PostAsJsonAsync($"{tasksBaseUrl}/api/boards", new
            {
                name = boardName,
                description = boardDescription
            });
            boardResp.EnsureSuccessStatusCode();
            var boardJson = await boardResp.Content.ReadFromJsonAsync<JsonElement>();
            var boardId = boardJson.GetProperty("id").GetInt32();
            var boardUrl = $"{tasksBaseUrl}/board/{boardId}";

            // 2. Get swimlanes to find backlog ID
            var swimlanesResp = await tasksHttp.GetAsync($"{tasksBaseUrl}/api/boards/{boardId}/swimlanes");
            swimlanesResp.EnsureSuccessStatusCode();
            var swimlanesJson = await swimlanesResp.Content.ReadFromJsonAsync<JsonElement>();
            var backlogId = swimlanesJson.EnumerateArray()
                .FirstOrDefault(s => s.TryGetProperty("name", out var n) && n.GetString() == "backlog")
                .TryGetProperty("id", out var sid) ? sid.GetInt32() : (int?)null;

            // 3. Create initial tasks in backlog
            if (backlogId.HasValue)
            {
                foreach (var task in initialTasks.Take(8))
                {
                    await tasksHttp.PostAsJsonAsync($"{tasksBaseUrl}/api/items", new
                    {
                        boardId,
                        swimlaneId = backlogId.Value,
                        title = task
                    });
                }
            }

            // 4. Create GitHub repo (if token configured)
            string? repoUrl = null;
            string? repoName = null;
            if (!string.IsNullOrEmpty(githubToken))
            {
                var slug = Slugify(lead.Name);
                repoName = $"poc-{slug}";
                var ghHttp = _httpFactory.CreateClient();
                ghHttp.DefaultRequestHeaders.Add("Authorization", $"Bearer {githubToken}");
                ghHttp.DefaultRequestHeaders.Add("User-Agent", "LeadManager/1.0");
                ghHttp.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

                var readme = $"# {boardName}\n\n{boardDescription}\n\n" +
                    $"**Tasks board:** {boardUrl}\n\n" +
                    $"## Backlog\n\n" +
                    string.Join("\n", initialTasks.Take(8).Select(t => $"- [ ] {t}"));

                var repoBody = new
                {
                    name = repoName,
                    description = boardDescription,
                    @private = true,
                    auto_init = false
                };

                var repoResp = await ghHttp.PostAsJsonAsync("https://api.github.com/user/repos", repoBody);
                if (repoResp.IsSuccessStatusCode)
                {
                    var repoJson = await repoResp.Content.ReadFromJsonAsync<JsonElement>();
                    repoUrl = repoJson.GetProperty("html_url").GetString();

                    // Push initial README
                    var contentResp = await ghHttp.PutAsJsonAsync(
                        $"https://api.github.com/repos/{githubOwner}/{repoName}/contents/README.md",
                        new
                        {
                            message = "Initial PoC setup",
                            content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(readme))
                        });
                    _ = contentResp; // fire-and-forget if it fails
                }
                else
                {
                    var err = await repoResp.Content.ReadAsStringAsync();
                    _logger.LogWarning("GitHub repo creation failed for lead {Id}: {Err}", id, err);
                }
            }

            // 5. Create Jengo AGI ClickUp task so Jengo picks up the PoC
            string? agiTaskUrl = null;
            var clickupApiKey = _config["ClickUp:ApiKey"];
            var jengoListId = _config["ClickUp:JengoListId"] ?? "901215559249";
            if (!string.IsNullOrEmpty(clickupApiKey))
            {
                try
                {
                    var cuHttp = _httpFactory.CreateClient();
                    cuHttp.DefaultRequestHeaders.Add("Authorization", clickupApiKey);
                    var taskDesc = $"PoC aanmaken voor {lead.Name} ({lead.Sector}, {lead.City})\n\n" +
                        $"Board: {boardUrl}\n" +
                        (repoUrl != null ? $"GitHub: {repoUrl}\n" : "") +
                        $"\n{boardDescription}";
                    var cuResp = await cuHttp.PostAsJsonAsync(
                        $"https://api.clickup.com/api/v2/list/{jengoListId}/task",
                        new { name = boardName, description = taskDesc });
                    if (cuResp.IsSuccessStatusCode)
                    {
                        var cuJson = await cuResp.Content.ReadFromJsonAsync<JsonElement>();
                        agiTaskUrl = cuJson.TryGetProperty("url", out var u) ? u.GetString() : null;
                    }
                    else
                    {
                        var err = await cuResp.Content.ReadAsStringAsync();
                        _logger.LogWarning("Jengo AGI task creation failed for lead {Id}: {Err}", id, err);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Jengo AGI task creation failed for lead {Id}", id);
                }
            }

            // 6. Save to workflowData
            var data = ParseWorkflowData(lead.WorkflowDataJson);
            data["poc"] = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                boardId,
                boardUrl,
                boardName,
                repoUrl,
                repoName,
                agiTaskUrl,
                createdAt = DateTime.UtcNow
            }))!;
            lead.WorkflowDataJson = data.ToJsonString();
            lead.WorkflowStep = Math.Max(lead.WorkflowStep, 5);
            await _db.SaveChangesAsync();

            return Ok(new { boardId, boardUrl, boardName, repoUrl, repoName, agiTaskUrl, workflowDataJson = lead.WorkflowDataJson });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PoC creation failed for lead {Id}", id);
            return StatusCode(500, "PoC board kon niet worden aangemaakt");
        }
    }

    private static string Slugify(string name)
    {
        var s = name.ToLowerInvariant();
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[^a-z0-9\s-]", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", "-");
        s = s.Trim('-');
        if (s.Length > 40) s = s[..40].TrimEnd('-');
        return string.IsNullOrEmpty(s) ? "poc" : s;
    }

    // Helper: parse workflowDataJson or return empty JsonObject
    private static JsonObject ParseWorkflowData(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new JsonObject();
        try { return JsonNode.Parse(json)?.AsObject() ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    private string BuildLeadContext(Lead lead)
    {
        var parts = new List<string> { $"Bedrijf: {lead.Name}" };
        if (!string.IsNullOrEmpty(lead.Sector)) parts.Add($"Sector: {lead.Sector}");
        if (!string.IsNullOrEmpty(lead.City)) parts.Add($"Stad: {lead.City}");
        if (!string.IsNullOrEmpty(lead.OwnerName)) parts.Add($"Eigenaar: {lead.OwnerName}");
        if (!string.IsNullOrEmpty(lead.Description)) parts.Add($"Over het bedrijf: {lead.Description}");
        if (!string.IsNullOrEmpty(lead.Services)) parts.Add($"Diensten: {lead.Services}");
        if (!string.IsNullOrEmpty(lead.AiSummary)) parts.Add($"AI-samenvatting: {lead.AiSummary}");
        if (!string.IsNullOrEmpty(lead.SalesPitch)) parts.Add($"Sales pitch: {lead.SalesPitch}");
        if (!string.IsNullOrEmpty(lead.TargetAudience)) parts.Add($"Doelgroep: {lead.TargetAudience}");
        if (lead.EmployeeCount != null) parts.Add($"Medewerkers: {lead.EmployeeCount}");
        if (lead.FoundingYear.HasValue) parts.Add($"Opgericht: {lead.FoundingYear}");
        if (lead.GoogleRating.HasValue) parts.Add($"Google: {lead.GoogleRating}/5 ({lead.GoogleReviewCount} reviews)");
        return string.Join("\n", parts);
    }

    private async Task<string> CallOpenAiAsync(string apiKey, string systemPrompt, string userPrompt, string model, double temperature)
    {
        var http = _httpFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Content = JsonContent.Create(new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature,
            response_format = new { type = "json_object" }
        });

        var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "{}";
    }
}
