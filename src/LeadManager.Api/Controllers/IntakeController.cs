using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Models;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Controllers;

[Route("api/intake")]
[ApiController]
[Authorize]
public class IntakeController : ControllerBase
{
    private readonly LeadManagerDbContext _db;
    private readonly EstimationService _estimation;
    private readonly MinoxPocConnectorService _minoxPoc;

    public IntakeController(
        LeadManagerDbContext db,
        EstimationService estimation,
        MinoxPocConnectorService minoxPoc)
    {
        _db = db;
        _estimation = estimation;
        _minoxPoc = minoxPoc;
    }

    private string? UserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    // GET /api/intake?clientId=xxx
    [HttpGet]
    public async Task<IActionResult> GetIntakes([FromQuery] Guid? clientId)
    {
        var query = _db.ClientIntakes
            .Include(i => i.Client)
            .Where(i => i.CreatedByUserId == UserId);

        if (clientId.HasValue)
            query = query.Where(i => i.ClientId == clientId);

        var intakes = await query
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => MapDto(i))
            .ToListAsync();

        return Ok(intakes);
    }

    // GET /api/intake/{id}
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetIntake(Guid id)
    {
        var intake = await _db.ClientIntakes
            .Include(i => i.Client)
            .FirstOrDefaultAsync(i => i.Id == id && i.CreatedByUserId == UserId);

        if (intake == null) return NotFound();
        return Ok(MapDto(intake));
    }

    // POST /api/intake
    [HttpPost]
    public async Task<IActionResult> CreateIntake([FromBody] CreateIntakeDto dto)
    {
        var client = await _db.Clients
            .FirstOrDefaultAsync(c => c.Id == dto.ClientId && c.CreatedByUserId == UserId);

        if (client == null) return NotFound("Client not found");

        var intake = new ClientIntake
        {
            ClientId = dto.ClientId,
            ProductType = dto.ProductType,
            BundleType = dto.BundleType,
            Requirements = dto.Requirements,
            FirstTask = dto.FirstTask,
            AdditionalNotes = dto.AdditionalNotes,
            Status = IntakeStatus.Draft,
            CreatedByUserId = UserId
        };

        _db.ClientIntakes.Add(intake);

        // Create or update bundle for this client
        await EnsureBundleAsync(client, dto.BundleType);

        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetIntake), new { id = intake.Id }, MapDto(intake));
    }

    // PUT /api/intake/{id}
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateIntake(Guid id, [FromBody] UpdateIntakeDto dto)
    {
        var intake = await _db.ClientIntakes
            .FirstOrDefaultAsync(i => i.Id == id && i.CreatedByUserId == UserId);

        if (intake == null) return NotFound();
        if (intake.Status != IntakeStatus.Draft)
            return BadRequest("Can only update intakes in Draft status");

        if (dto.ProductType.HasValue) intake.ProductType = dto.ProductType.Value;
        if (dto.BundleType.HasValue) intake.BundleType = dto.BundleType.Value;
        if (dto.Requirements != null) intake.Requirements = dto.Requirements;
        if (dto.FirstTask != null) intake.FirstTask = dto.FirstTask;
        if (dto.AdditionalNotes != null) intake.AdditionalNotes = dto.AdditionalNotes;
        intake.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(MapDto(intake));
    }

    // POST /api/intake/{id}/submit — triggers AI estimation
    [HttpPost("{id:guid}/submit")]
    public async Task<IActionResult> SubmitIntake(Guid id)
    {
        var intake = await _db.ClientIntakes
            .Include(i => i.Client)
            .FirstOrDefaultAsync(i => i.Id == id && i.CreatedByUserId == UserId);

        if (intake == null) return NotFound();
        if (intake.Status != IntakeStatus.Draft)
            return BadRequest("Intake already submitted");

        // Run AI estimation
        var estimate = await _estimation.EstimateAsync(intake);
        intake.EstimatedHours = estimate.EstimatedHours;
        intake.EstimatedPrice = estimate.EstimatedPrice;
        intake.EstimationReasoning = estimate.Reasoning;
        intake.Status = IntakeStatus.Estimated;
        intake.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(MapDto(intake));
    }

    // POST /api/intake/{id}/approve
    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> ApproveIntake(Guid id, [FromBody] ApproveIntakeDto dto)
    {
        var intake = await _db.ClientIntakes
            .Include(i => i.Client)
            .FirstOrDefaultAsync(i => i.Id == id && i.CreatedByUserId == UserId);

        if (intake == null) return NotFound();
        if (intake.Status != IntakeStatus.Estimated)
            return BadRequest("Intake must be in Estimated status to approve");

        if (!dto.Approved)
        {
            intake.Status = IntakeStatus.Draft;
            intake.EstimatedHours = null;
            intake.EstimatedPrice = null;
            intake.EstimationReasoning = dto.RejectionReason;
            intake.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(MapDto(intake));
        }

        // Client approved — push to minox-poc work queue
        intake.Status = IntakeStatus.Approved;
        intake.ApprovedAt = DateTime.UtcNow;
        intake.ApprovedByUserId = UserId;
        intake.UpdatedAt = DateTime.UtcNow;

        // Deduct estimated hours from bundle
        await DeductBundleHoursAsync(intake.ClientId, intake.EstimatedHours ?? 0);

        await _db.SaveChangesAsync();

        // Send to minox-poc (fire and forget — don't block approval on this)
        _ = Task.Run(async () =>
        {
            var workItemId = await _minoxPoc.CreateWorkItemAsync(intake, intake.Client.Name);
            if (workItemId != null)
            {
                intake.WorkQueueItemId = workItemId;
                intake.Status = IntakeStatus.InProgress;
                intake.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
        });

        return Ok(MapDto(intake));
    }

    // GET /api/intake/bundles?clientId=xxx
    [HttpGet("bundles")]
    public async Task<IActionResult> GetBundles([FromQuery] Guid? clientId)
    {
        var query = _db.ClientBundles
            .Where(b => b.CreatedByUserId == UserId && b.IsActive);

        if (clientId.HasValue)
            query = query.Where(b => b.ClientId == clientId);

        var bundles = await query
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => MapBundleDto(b))
            .ToListAsync();

        return Ok(bundles);
    }

    private async Task EnsureBundleAsync(Client client, IntakeBundleType bundleType)
    {
        var existing = await _db.ClientBundles
            .FirstOrDefaultAsync(b => b.ClientId == client.Id && b.IsActive);

        if (existing != null) return; // already has a bundle

        var now = DateTime.UtcNow;
        var bundle = new ClientBundle
        {
            ClientId = client.Id,
            BundleType = bundleType,
            CreatedByUserId = UserId,
            PeriodStart = now,
            PeriodEnd = now.AddMonths(1)
        };

        switch (bundleType)
        {
            case IntakeBundleType.Starter:
                bundle.TotalHours = 50;
                bundle.MonthlyPrice = 125m;
                bundle.HourlyRate = 2.50m;
                break;
            case IntakeBundleType.Team:
                bundle.TotalHours = 200;
                bundle.MonthlyPrice = 500m;
                bundle.HourlyRate = 2.50m;
                break;
            case IntakeBundleType.PayPerHour:
                bundle.TotalHours = 0;
                bundle.MonthlyPrice = 0;
                bundle.HourlyRate = 3.00m;
                break;
        }

        _db.ClientBundles.Add(bundle);
    }

    private async Task DeductBundleHoursAsync(Guid clientId, decimal hours)
    {
        var bundle = await _db.ClientBundles
            .FirstOrDefaultAsync(b => b.ClientId == clientId && b.IsActive);

        if (bundle != null)
            bundle.HoursUsed += hours;
    }

    private static IntakeDto MapDto(ClientIntake i) => new(
        i.Id,
        i.ClientId,
        i.Client?.Name ?? "",
        i.ProductType,
        i.BundleType,
        i.Status,
        i.Requirements,
        i.FirstTask,
        i.AdditionalNotes,
        i.EstimatedHours,
        i.EstimatedPrice,
        i.EstimationReasoning,
        i.ApprovedAt,
        i.WorkQueueItemId,
        i.CreatedAt,
        i.UpdatedAt
    );

    private static ClientBundleDto MapBundleDto(ClientBundle b) => new(
        b.Id,
        b.ClientId,
        b.BundleType,
        b.TotalHours,
        b.HoursUsed,
        b.HoursRemaining,
        b.MonthlyPrice,
        b.HourlyRate,
        b.PeriodStart,
        b.PeriodEnd,
        b.IsActive,
        b.IsOverBudget
    );
}
