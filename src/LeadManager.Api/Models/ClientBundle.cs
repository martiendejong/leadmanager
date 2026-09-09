namespace LeadManager.Api.Models;

public class ClientBundle
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Client Client { get; set; } = null!;

    public IntakeBundleType BundleType { get; set; }
    public int TotalHours { get; set; }       // 50 or 200 (or 0 for PayPerHour)
    public decimal HoursUsed { get; set; } = 0;
    public decimal MonthlyPrice { get; set; } // 125 or 500 or 0
    public decimal HourlyRate { get; set; }   // 2.50 or 3.00

    public DateTime PeriodStart { get; set; } = DateTime.UtcNow;
    public DateTime PeriodEnd { get; set; }   // PeriodStart + 1 month
    public bool IsActive { get; set; } = true;

    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public decimal HoursRemaining => Math.Max(0, TotalHours - HoursUsed);
    public bool IsOverBudget => HoursUsed > TotalHours;
}
