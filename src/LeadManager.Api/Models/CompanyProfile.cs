namespace LeadManager.Api.Models;

public class CompanyProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public string WebsiteUrl { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string Description { get; set; } = "";
    public string WhatTheyDo { get; set; } = "";
    public string IdealCustomerProfile { get; set; } = "";
    public string ToneOfVoice { get; set; } = "";
    public string TargetSectorsJson { get; set; } = "[]";   // string[]
    public string TargetRegionsJson { get; set; } = "[]";   // string[]
    public string KeywordsJson { get; set; } = "[]";        // string[]
    public string UspsJson { get; set; } = "[]";            // string[]
    public DateTime? CrawledAt { get; set; }
    public int ProfileVersion { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
