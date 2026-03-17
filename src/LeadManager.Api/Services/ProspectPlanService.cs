using LeadManager.Api.Data;
using LeadManager.Api.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace LeadManager.Api.Services;

public class ProspectPlanService
{
    private readonly LeadManagerDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;

    public ProspectPlanService(LeadManagerDbContext db, IConfiguration configuration, HttpClient httpClient)
    {
        _db = db;
        _configuration = configuration;
        _httpClient = httpClient;
    }

    public async Task<string> GenerateProspectPlan(Guid leadId, string userId)
    {
        // Get lead with notes
        var lead = await _db.Leads
            .FirstOrDefaultAsync(l => l.Id == leadId && l.ImportedByUserId == userId);

        if (lead == null)
            throw new Exception("Lead not found");

        // Get all notes for this lead
        // Note: LeadNotes feature will be available after PR #24 is merged
        // For now, generating plan based on lead information only
        var notesContext = "Geen notities beschikbaar (notities feature wordt toegevoegd in PR #24).";

        var leadContext = $@"
Bedrijf: {lead.Name}
Sector: {lead.Sector}
Stad: {lead.City}
Website: {lead.Website}

{(lead.Description != null ? $"Beschrijving: {lead.Description}\n" : "")}
{(lead.Services != null ? $"Diensten: {lead.Services}\n" : "")}
{(lead.TargetAudience != null ? $"Doelgroep: {lead.TargetAudience}\n" : "")}
{(lead.AiSummary != null ? $"AI Samenvatting: {lead.AiSummary}\n" : "")}
{(lead.SalesPitch != null ? $"Salespitch: {lead.SalesPitch}\n" : "")}

Notities en gesprekken:
{notesContext}
";

        var prompt = $@"Je bent een ervaren sales strategist. Op basis van de onderstaande informatie over een prospect, genereer een gestructureerd actieplan om dit bedrijf als klant binnen te halen.

{leadContext}

Maak een helder, uitvoerbaar actieplan met de volgende structuur:

## Doelstelling
[Wat is het primaire doel met deze prospect?]

## Aanpak
[Welke strategische stappen nemen we?]

## Volgende Acties
1. [Concrete actie met timing]
2. [Concrete actie met timing]
3. [Concrete actie met timing]

## Verwacht Resultaat
[Wat verwachten we te bereiken?]

## Risico's & Mitigatie
[Mogelijke obstakels en hoe we die aanpakken]

Wees specifiek, praktisch en actionable. Gebruik de notities en gespreksinformatie om gepersonaliseerde aanbevelingen te doen.";

        // Call OpenAI API
        var openAiKey = _configuration["OpenAI:ApiKey"]
            ?? throw new Exception("OpenAI API key not configured");

        var requestBody = new
        {
            model = "gpt-4",
            messages = new[]
            {
                new { role = "system", content = "Je bent een ervaren sales strategist die actionable prospect plannen maakt." },
                new { role = "user", content = prompt }
            },
            temperature = 0.7,
            max_tokens = 2000
        };

        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", openAiKey);

        var response = await _httpClient.PostAsJsonAsync(
            "https://api.openai.com/v1/chat/completions",
            requestBody);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"OpenAI API error: {error}");
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var plan = result
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(plan))
            throw new Exception("OpenAI returned empty response");

        return plan.Trim();
    }
}
