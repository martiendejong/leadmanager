using LeadManager.Api.Models;

namespace LeadManager.Api.Services;

public record DueItem(
    Guid ServiceId, Guid ClientId, string ServiceName, string Interval,
    DateTime? PeriodStart, DateTime? PeriodEnd, decimal? AmountExclVat, decimal VatRate, decimal? AmountInclVat,
    string State, int PeriodsOverdue, bool IsFree, DateTime? FreeUntil, DateTime? CoveredThrough, string Explanation);

/// <summary>
/// Works out, per service, what has to be invoiced next from the agreed terms and the invoices already sent.
/// Pure logic: no database, no clock. States: Due (period has started), Upcoming (within the look-ahead),
/// Covered (prepaid or free beyond the look-ahead), NeedsInput (terms incomplete or hourly), Nothing (not active,
/// ended, or one-off already invoiced). Periods are invoiced in advance; PeriodEnd is inclusive.
/// </summary>
public static class BillingCalculator
{
    public const decimal DefaultVat = 21m;

    public static int Months(BillingInterval i) => i switch
    {
        BillingInterval.Monthly => 1, BillingInterval.Quarterly => 3, BillingInterval.Yearly => 12, _ => 0,
    };

    public static decimal? PeriodAmount(ClientService s)
    {
        if (s.BillingAmount.HasValue) return s.BillingAmount;
        if (s.MonthlyAmount.HasValue && Months(s.BillingInterval) > 0) return Math.Round(s.MonthlyAmount.Value * Months(s.BillingInterval), 2);
        return null;
    }

    public static DueItem Compute(ClientService s, IEnumerable<ClientInvoice> invoices, DateTime today, int lookAheadDays = 30)
    {
        today = today.Date;
        var vat = s.VatRate ?? DefaultVat;
        DueItem Make(string state, string why, DateTime? ps = null, DateTime? pe = null, decimal? amt = null, int overdue = 0, DateTime? covered = null) =>
            new(s.Id, s.ClientId, s.Name, s.BillingInterval.ToString(), ps, pe, amt, vat,
                amt.HasValue ? Math.Round(amt.Value * (1 + vat / 100m), 2) : null, state, overdue,
                s.FreeUntil.HasValue && s.FreeUntil.Value.Date >= today, s.FreeUntil, covered, why);

        if (s.Status != ServiceStatus.Active)
            return Make("Nothing", $"Service is {s.Status}; nothing to invoice.");

        var counted = invoices.Where(i => i.Status is InvoiceStatus.Sent or InvoiceStatus.Paid).ToList();

        if (s.BillingInterval == BillingInterval.Hourly)
            return Make("NeedsInput", s.HourlyRate.HasValue
                ? $"Hourly at EUR {s.HourlyRate:0.00} excl. VAT: the number of hours is needed to invoice."
                : "Hourly billing without a rate: add the hourly rate.");

        var amount = PeriodAmount(s);

        if (s.BillingInterval == BillingInterval.OneOff)
        {
            if (counted.Count > 0) return Make("Nothing", $"One-off service already invoiced ({counted[0].Number}).");
            if (!amount.HasValue) return Make("NeedsInput", "One-off service without an amount.");
            if (s.FreeUntil.HasValue && s.FreeUntil.Value.Date >= today)
                return Make("Covered", $"Free ({s.FreeReason ?? "free of charge"}) until {s.FreeUntil:yyyy-MM-dd}.", covered: s.FreeUntil);
            return Make("Due", $"One-off amount of EUR {amount:0.00} excl. VAT, not invoiced yet.", s.StartDate, s.StartDate, amount, 1);
        }

        if (!amount.HasValue) return Make("NeedsInput", "No amount agreed for this service yet.");
        var step = Months(s.BillingInterval);

        // The day up to which everything is settled: prepaid, free, or covered by a sent invoice.
        DateTime? covered = null;
        void Take(DateTime? d) { if (d.HasValue && (!covered.HasValue || d.Value.Date > covered.Value)) covered = d.Value.Date; }
        Take(s.PaidUntil);
        Take(s.FreeUntil);
        foreach (var i in counted) Take(i.PeriodEnd);

        DateTime start;
        if (covered.HasValue) start = covered.Value.AddDays(1);
        else if (s.StartDate.HasValue) start = s.StartDate.Value.Date;
        else return Make("NeedsInput", "No start date and nothing invoiced yet: add the start date.", amt: amount);

        if (s.StartDate.HasValue && start < s.StartDate.Value.Date) start = s.StartDate.Value.Date;

        if (s.EndDate.HasValue && start > s.EndDate.Value.Date)
            return Make("Nothing", $"Service ended on {s.EndDate:yyyy-MM-dd}; everything is invoiced.", covered: covered);

        var fullEnd = start.AddMonths(step).AddDays(-1);
        var end = fullEnd;
        if (s.EndDate.HasValue && end > s.EndDate.Value.Date) end = s.EndDate.Value.Date;
        var prorated = end < fullEnd;
        var periodAmount = amount;
        if (prorated)
        {
            var full = (fullEnd - start).TotalDays + 1;
            periodAmount = Math.Round(amount!.Value * (decimal)(((end - start).TotalDays + 1) / full), 2);
        }

        var overdue = 0;
        for (var ps = start; ps <= today && overdue < 60; ps = ps.AddMonths(step)) overdue++;

        var covNote = covered.HasValue
            ? $" Settled up to {covered:yyyy-MM-dd}" + (s.FreeUntil.HasValue && covered == s.FreeUntil.Value.Date ? $" (free: {s.FreeReason ?? "free of charge"})" : "") + "."
            : "";
        var line = $"EUR {periodAmount:0.00} excl. VAT for {start:yyyy-MM-dd} to {end:yyyy-MM-dd} ({s.BillingInterval.ToString().ToLowerInvariant()}).{covNote}";
        if (prorated) line += " Last period is prorated to the end date.";

        if (start <= today) return Make("Due", line + (overdue > 1 ? $" {overdue} periods are open." : ""), start, end, periodAmount, overdue, covered);
        if ((start - today).TotalDays <= lookAheadDays) return Make("Upcoming", $"Next invoice from {start:yyyy-MM-dd}. " + line, start, end, periodAmount, 0, covered);
        return Make("Covered", $"Nothing to invoice before {start:yyyy-MM-dd}. " + line, start, end, periodAmount, 0, covered);
    }
}
