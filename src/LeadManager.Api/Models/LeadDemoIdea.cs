namespace LeadManager.Api.Models;

/// <summary>
/// One suggested demo for a lead: something we could build to show the business what we can do for it. Written by the AI from the stored
/// facts of the lead only. The latest set per user and lead is kept (a new generation replaces the old one), so it survives a reload.
/// The instruction prompt is text the user copies into a coding or AI agent; the app never runs it.
/// </summary>
public class LeadDemoIdea
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LeadId { get; set; }
    public Lead? Lead { get; set; }
    public string UserId { get; set; } = "";
    /// <summary>Generations replace each other: every row of one generation has the same batch id.</summary>
    public Guid BatchId { get; set; }
    public int Position { get; set; }
    public string Title { get; set; } = "";
    /// <summary>What the demo shows.</summary>
    public string Shows { get; set; } = "";
    /// <summary>Why it fits THIS lead, in terms of its stored facts.</summary>
    public string WhyFit { get; set; } = "";
    /// <summary>A key of the product catalog ("custom" for bespoke work).</summary>
    public string ProductKey { get; set; } = "";
    /// <summary>The prompt for a coding or AI agent that builds the demo.</summary>
    public string Prompt { get; set; } = "";
    /// <summary>What the user asked for when generating (may be empty).</summary>
    public string Instruction { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}
