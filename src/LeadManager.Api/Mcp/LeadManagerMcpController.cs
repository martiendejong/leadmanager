using System.Security.Claims;
using System.Text.Json;
using JengoComponents.McpConnector;
using LeadManager.Api.Controllers;
using LeadManager.Api.Data;
using LeadManager.Api.DTOs;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeadManager.Api.Mcp;

/// <summary>
/// MCP server of Jengo Clients: lets agents and other Jengo tools read and write leads, clients,
/// conversations, next actions and links.
///
/// Every tool calls the same controllers as the REST API, as the user behind the API key, so there is exactly
/// one set of rules: ownership, validation, limits, idempotency (SourceRef), UTC timestamps. A tool can never
/// do more than the REST endpoint it wraps.
/// </summary>
[RequestSizeLimit(McpRequestGuard.MaxBodyBytes)]
public class LeadManagerMcpController : McpControllerBase
{
    private static readonly HashSet<string> KnownTools = new()
    {
        "search_leads", "find_by_email_or_domain", "get_lead", "create_lead", "log_interaction",
        "add_next_action", "complete_next_action", "next_actions_today", "add_contact", "link_item",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly LeadManagerDbContext _db;
    private readonly CrmParentAccess _access;
    private readonly ILogger<LeadManagerMcpController> _logger;

    public LeadManagerMcpController(
        McpConnectorOptions options, IMcpTokenValidator tokens,
        LeadManagerDbContext db, CrmParentAccess access, ILogger<LeadManagerMcpController> logger)
        : base(options, tokens)
    {
        _db = db;
        _access = access;
        _logger = logger;
    }

    // ---- tool catalogue -------------------------------------------------------------------------------

    private static object Str(string description) => new { type = "string", description };

    private static object Tool(string name, string description, object properties, params string[] required) => new
    {
        name,
        description,
        inputSchema = new { type = "object", properties, required },
    };

    private static readonly object ParentTypeSchema =
        new { type = "string", @enum = new[] { "leads", "clients" }, description = "leads (default) or clients" };

    private static readonly object ParentIdSchema = Str("Id of the lead or client");

    private static readonly object ParentProps = new { parent_type = ParentTypeSchema, parent_id = ParentIdSchema };

    protected override IReadOnlyList<object> GetTools(McpPrincipal principal) =>
    [
        Tool("search_leads",
            "Search the leads and clients of the user by name, e-mail address or website domain. Returns the best matches first.",
            new { query = Str("Name, part of a name"), email = Str("E-mail address (also finds contacts)"), domain = Str("Website domain, e.g. hogenkamp.nl") }),
        Tool("find_by_email_or_domain",
            "Who is this? Resolve an e-mail address or a domain to the lead or client it belongs to.",
            new { email = Str("E-mail address"), domain = Str("Website domain") }),
        Tool("get_lead",
            "Everything about one lead or client: details, contacts, open next actions, links and the latest history.",
            ParentProps, "parent_id"),
        Tool("create_lead",
            "Create a lead, or return the existing one when the website domain is already known. Safe to repeat.",
            new { name = Str("Company name"), website = Str("Website (https:// is added when missing)"), sector = Str("Sector"),
                  city = Str("City"), phone = Str("Phone number"), email = Str("Company e-mail address"), source = Str("Where this lead comes from") },
            "name"),
        Tool("log_interaction",
            "Record a conversation: a call, mail, WhatsApp message, meeting or note. A first outbound touch moves a new lead to Contacted.",
            new
            {
                parent_type = ParentTypeSchema, parent_id = ParentIdSchema,
                kind = new { type = "string", @enum = new[] { "Call", "Email", "WhatsApp", "Meeting", "Note" } },
                summary = Str("What was said or agreed"),
                direction = new { type = "string", @enum = new[] { "Outbound", "Inbound", "Internal" }, description = "Default Outbound (Internal for notes)" },
                outcome = Str("Short outcome, e.g. geen gehoor, terugbellen, interesse"),
                occurred_at = Str("ISO 8601 time; default now. Without a zone it is read as UTC."),
                source_ref = Str("Id in the source system (e.g. mail:3:Sent:409). Logging the same source_ref twice returns the first."),
                contact_id = Str("Optional contact of this lead or client"),
            },
            "parent_id", "kind", "summary"),
        Tool("add_next_action",
            "Plan the next step for a lead or client. It shows up on the Vandaag list when it is due.",
            new
            {
                parent_type = ParentTypeSchema, parent_id = ParentIdSchema,
                title = Str("What to do, e.g. terugbellen over de offerte"),
                due_at = Str("ISO 8601 time. Without a zone it is read as UTC."),
                contact_id = Str("Optional contact"),
            },
            "parent_id", "title", "due_at"),
        Tool("complete_next_action", "Mark a next action as done.", new { id = Str("Id of the next action") }, "id"),
        Tool("next_actions_today",
            "The call list: open next actions due today (Dutch time) or overdue, with name and phone number.",
            new { }),
        Tool("add_contact",
            "Add a person to a lead or client.",
            new
            {
                parent_type = ParentTypeSchema, parent_id = ParentIdSchema,
                name = Str("Full name"), role = Str("Role"), email = Str("E-mail"), phone = Str("Phone"), mobile = Str("Mobile"),
            },
            "parent_id", "name"),
        Tool("link_item",
            "Link a lead or client to something in another Jengo tool: a JengoWork board or task, a JengoKnowledge project, a mail thread, a Meet session.",
            new
            {
                parent_type = ParentTypeSchema, parent_id = ParentIdSchema,
                kind = new { type = "string", @enum = Enum.GetNames<Models.EntityLinkKind>() },
                label = Str("Name shown to the user"),
                url = Str("http(s) URL"),
                external_id = Str("Id in the other system"),
            },
            "parent_id", "kind", "label"),
    ];

    // ---- tool execution -------------------------------------------------------------------------------

    protected override async Task<(string text, bool isError)> CallToolAsync(
        string name, JsonElement args, McpPrincipal principal, CancellationToken ct)
    {
        // The name comes from the caller: log it only when it is a tool we know, so a hostile client cannot
        // forge log lines or bloat the log with it.
        _logger.LogInformation("MCP tool {Tool} for user {UserId}", KnownTools.Contains(name) ? name : "(unknown)", principal.Subject);
        var userId = principal.Subject;

        try
        {
            switch (name)
            {
                case "search_leads":
                    return Present(await As(new CrmSearchController(_db, _access), userId)
                        .Find(ArgStr(args, "email"), ArgStr(args, "domain"), ArgStr(args, "query")));

                case "find_by_email_or_domain":
                    return Present(await As(new CrmSearchController(_db, _access), userId)
                        .Find(ArgStr(args, "email"), ArgStr(args, "domain"), null));

                case "get_lead":
                    return await GetLeadAsync(args, userId);

                case "create_lead":
                {
                    var lead = new ImportLeadDto(ArgStr(args, "name") ?? "", ArgStr(args, "website"), ArgStr(args, "sector"),
                        ArgStr(args, "city"), ArgStr(args, "phone"), ArgStr(args, "email"), ArgStr(args, "source") ?? "mcp",
                        null, null, null);
                    var result = await As(new CrmImportController(_db, _access), userId)
                        .Import(new ImportRequestDto(new List<ImportLeadDto> { lead }), dryRun: false);
                    if (result is OkObjectResult { Value: ImportReportDto report })
                    {
                        var row = report.Leads[0];
                        return row.Action == "invalid"
                            ? (string.Join("; ", row.Problems), true)
                            : (Serialize(new { action = row.Action, id = row.LeadId, name = row.Name }), false);
                    }
                    return Present(result);
                }

                case "log_interaction":
                {
                    var parent = ParentArgs(args, out var error);
                    if (parent == null) return (error!, true);
                    if (!TryDate(args, "occurred_at", out var at, out error)) return (error!, true);
                    return Present(await As(new InteractionsController(_db, _access), userId).Create(
                        parent.Value.type, parent.Value.id,
                        new CreateInteractionDto(ArgStr(args, "kind") ?? "", ArgStr(args, "direction"), at,
                            ArgStr(args, "summary") ?? "", ArgStr(args, "outcome"), ArgGuid(args, "contact_id"), ArgStr(args, "source_ref"))));
                }

                case "add_next_action":
                {
                    var parent = ParentArgs(args, out var error);
                    if (parent == null) return (error!, true);
                    if (!TryDate(args, "due_at", out var due, out error) || due == null)
                        return (error ?? "due_at is required (ISO 8601)", true);
                    return Present(await As(new NextActionsController(_db, _access), userId).Create(
                        parent.Value.type, parent.Value.id,
                        new CreateNextActionDto(ArgStr(args, "title") ?? "", due.Value, ArgGuid(args, "contact_id"), null)));
                }

                case "complete_next_action":
                {
                    var id = ArgGuid(args, "id");
                    if (id == null) return ("id is required", true);
                    return Present(await As(new NextActionsController(_db, _access), userId).Complete(id.Value));
                }

                case "next_actions_today":
                    return Present(await As(new NextActionsController(_db, _access), userId).Today());

                case "add_contact":
                {
                    var parent = ParentArgs(args, out var error);
                    if (parent == null) return (error!, true);
                    return Present(await As(new ContactsController(_db, _access), userId).Create(
                        parent.Value.type, parent.Value.id,
                        new UpsertContactDto(ArgStr(args, "name") ?? "", ArgStr(args, "role"), ArgStr(args, "email"),
                            ArgStr(args, "phone"), ArgStr(args, "mobile"), null, null, null)));
                }

                case "link_item":
                {
                    var parent = ParentArgs(args, out var error);
                    if (parent == null) return (error!, true);
                    return Present(await As(new EntityLinksController(_db, _access), userId).Create(
                        parent.Value.type, parent.Value.id,
                        new CreateEntityLinkDto(ArgStr(args, "kind") ?? "", ArgStr(args, "label") ?? "", ArgStr(args, "url"), ArgStr(args, "external_id"))));
                }

                default:
                    return ($"Unknown tool: {name}", true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "MCP tool {Tool} failed", name);
            return ("The tool failed unexpectedly. Nothing was changed that you can rely on; check the result before retrying.", true);
        }
    }

    private async Task<(string text, bool isError)> GetLeadAsync(JsonElement args, string userId)
    {
        var parent = ParentArgs(args, out var error);
        if (parent == null) return (error!, true);

        var resolved = await _access.ResolveAsync(parent.Value.type, parent.Value.id, userId);
        if (resolved == null) return ("Not found, or you do not have access.", true);

        object details;
        if (resolved.Value.Kind == CrmParentKind.Lead)
        {
            details = await _db.Leads.Where(l => l.Id == parent.Value.id).Select(l => new
            {
                type = "lead", l.Id, l.Name, l.Website, l.Sector, l.City, l.Phone, l.CompanyEmail, l.OwnerName, l.OwnerMobile,
                status = l.Status.ToString(), pipeline = l.PipelineStatus.ToString(), l.Source, l.AiSummary,
            }).FirstAsync();
        }
        else
        {
            details = await _db.Clients.Where(c => c.Id == parent.Value.id).Select(c => new
            {
                type = "client", c.Id, c.Name, c.Website, c.Sector, c.City, c.PrimaryContactName, c.PrimaryContactEmail,
                c.PrimaryContactPhone, c.Plan, c.IsActive,
            }).FirstAsync();
        }

        object? Value(IActionResult r) => r is ObjectResult o ? o.Value : null;
        var t = parent.Value.type;
        var id = parent.Value.id;
        var contacts = Value(await As(new ContactsController(_db, _access), userId).List(t, id));
        var actions = Value(await As(new NextActionsController(_db, _access), userId).List(t, id, false));
        var links = Value(await As(new EntityLinksController(_db, _access), userId).List(t, id));
        var timeline = Value(await As(new InteractionsController(_db, _access), userId).Timeline(t, id, 15));

        return (Serialize(new { details, contacts, open_next_actions = actions, links, latest_history = timeline }), false);
    }

    // ---- helpers --------------------------------------------------------------------------------------

    /// <summary>Gives a controller the identity of the key's user, so the usual ownership rules apply.</summary>
    private static T As<T>(T controller, string userId) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "mcp")),
            },
        };
        return controller;
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Json);

    /// <summary>Turns a controller result into MCP tool output.</summary>
    private static (string text, bool isError) Present(IActionResult result) => result switch
    {
        ObjectResult { StatusCode: >= 400 } o => (o.Value as string ?? Serialize(o.Value ?? "Request failed"), true),
        ObjectResult o => (Serialize(o.Value ?? new { }), false),
        NotFoundResult => ("Not found, or you do not have access.", true),
        NoContentResult => ("Done.", false),
        StatusCodeResult s when s.StatusCode >= 400 => ($"Request failed ({s.StatusCode}).", true),
        _ => ("Done.", false),
    };

    private static (string type, Guid id)? ParentArgs(JsonElement args, out string? error)
    {
        error = null;
        var type = (ArgStr(args, "parent_type") ?? "leads").Trim().ToLowerInvariant();
        if (type != "leads" && type != "clients") { error = "parent_type must be leads or clients"; return null; }
        var id = ArgGuid(args, "parent_id");
        if (id == null) { error = "parent_id is required and must be a GUID"; return null; }
        return (type, id.Value);
    }

    private static Guid? ArgGuid(JsonElement args, string name) =>
        Guid.TryParse(ArgStr(args, name), out var g) ? g : null;

    /// <summary>Parses an optional ISO 8601 argument. A value without a zone is taken as UTC.</summary>
    private static bool TryDate(JsonElement args, string name, out DateTime? value, out string? error)
    {
        value = null;
        error = null;
        var text = ArgStr(args, name);
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
        {
            error = $"{name} is not a valid ISO 8601 date/time";
            return false;
        }
        value = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }
}
