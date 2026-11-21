using LeadManager.Api.Models;

namespace LeadManager.Api.Data;

/// <summary>
/// The organisations that exist from the start (model seed data, also inserted by the migration). The ids are fixed so
/// the migration can assign existing leads without looking anything up. More organisations are created in the app.
/// </summary>
public static class OrganizationSeed
{
    public static readonly Guid Prospergenics = new("6f0a7c10-0000-4000-8000-000000000001");
    public static readonly Guid MartienDeJong = new("6f0a7c10-0000-4000-8000-000000000002");
    public static readonly Guid PortOfGiethoorn = new("6f0a7c10-0000-4000-8000-000000000003");
    public static readonly Guid KaribuMara = new("6f0a7c10-0000-4000-8000-000000000004");
    public static readonly Guid RenaFarmSystem = new("6f0a7c10-0000-4000-8000-000000000005");
    public static readonly Guid MaaLinkRide = new("6f0a7c10-0000-4000-8000-000000000006");
    public static readonly Guid WeerzienMara = new("6f0a7c10-0000-4000-8000-000000000007");

    private static readonly DateTime Created = new(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);

    public static IEnumerable<Organization> All() => new[]
    {
        new Organization { Id = Prospergenics, Name = "Prospergenics", Slug = "prospergenics", CreatedAt = Created },
        new Organization { Id = MartienDeJong, Name = "Martien de Jong", Slug = "martien-de-jong", CreatedAt = Created },
        new Organization { Id = PortOfGiethoorn, Name = "Port of Giethoorn", Slug = "port-of-giethoorn", CreatedAt = Created },
        new Organization { Id = KaribuMara, Name = "Karibu Mara", Slug = "karibu-mara", CreatedAt = Created },
        new Organization { Id = RenaFarmSystem, Name = "Rena Farm system", Slug = "rena-farm-system", CreatedAt = Created },
        new Organization { Id = MaaLinkRide, Name = "MaaLink Ride", Slug = "maalink-ride", CreatedAt = Created },
        new Organization { Id = WeerzienMara, Name = "Weerzien Mara", Slug = "weerzien-mara", CreatedAt = Created },
    };
}
