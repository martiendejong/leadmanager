namespace LeadManager.Api.Models;

public static class HuntKinds
{
    public const string Hunt = "Hunt";
    public const string Verify = "Verify";
}

public static class HuntStatuses
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";

    public static bool IsActive(string s) => s == Pending || s == Running;
}

public static class HuntOutcomes
{
    public const string Qualified = "Qualified";           // verified score reached the minimum
    public const string BelowMinimum = "BelowMinimum";     // fully researched, verified score is below the minimum
    public const string StoppedEarly = "StoppedEarly";     // the quick pass showed the score cannot reach the bar
    public const string Duplicate = "Duplicate";           // already a lead of this user
    public const string Unreachable = "Unreachable";       // website does not answer
    public const string Verified = "Verified";             // a verify job rescored an existing lead
    public const string Error = "Error";
    public const string Rejected = "Rejected";             // the agent submitted something the server could not use
}

/// <summary>
/// A background run: a lead hunt (a Jengo agent researches new companies, the server verifies what it submits) or a verify and
/// rescore of existing leads (done by the server from the leads' own websites).
/// </summary>
public class HuntJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? UserId { get; set; }
    public string Kind { get; set; } = HuntKinds.Hunt;
    public string Status { get; set; } = HuntStatuses.Pending;
    public bool CancelRequested { get; set; }

    // Criteria (hunt)
    public string Segment { get; set; } = "";
    public string Region { get; set; } = "";
    public int MinScore { get; set; } = 8;
    public int TargetCount { get; set; } = 3;
    public int MaxCandidates { get; set; } = 30;
    public bool Ratchet { get; set; } = true;

    /// <summary>Verify jobs: the leads to verify (stored as JSON).</summary>
    public List<Guid> LeadIds { get; set; } = new();

    // Progress
    public int Total { get; set; }
    public int Screened { get; set; }
    public int Qualified { get; set; }
    public int BestScore { get; set; }
    public string? CurrentCandidate { get; set; }
    public string? Error { get; set; }

    /// <summary>Hunt jobs: the JengoWork task that started the background agent, and the last time the agent reported.</summary>
    public string? TaskId { get; set; }
    public string? TaskUrl { get; set; }
    public DateTime? LastAgentCallAt { get; set; }
    /// <summary>The agent's own short summary when it finished.</summary>
    public string? Summary { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public ICollection<HuntCandidate> Candidates { get; set; } = new List<HuntCandidate>();
}

/// <summary>One company looked at by a job, with what was verified and why it stopped.</summary>
public class HuntCandidate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public Guid? LeadId { get; set; }
    public string Name { get; set; } = "";
    public string Website { get; set; } = "";
    public int? Score { get; set; }
    /// <summary>The highest score the quick pass thought possible (an upper bound, not a claim).</summary>
    public int? MaxReachable { get; set; }
    public string Outcome { get; set; } = HuntOutcomes.Error;
    public string Reason { get; set; } = "";
    /// <summary>JSON: the verified claims with source and quote, what was dropped and why.</summary>
    public string EvidenceJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public HuntJob? Job { get; set; }
}
