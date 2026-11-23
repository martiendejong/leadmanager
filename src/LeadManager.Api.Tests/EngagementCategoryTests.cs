using LeadManager.Api.Models;
using Xunit;

namespace LeadManager.Api.Tests;

public class EngagementCategoryTests
{
    [Theory]
    [InlineData("Active")]
    [InlineData("Maintenance")]
    [InlineData("Reactive")]
    [InlineData("Prospect")]
    [InlineData("WarmLead")]
    [InlineData("OneOff")]
    [InlineData("Conflict")]
    [InlineData("RelationshipEnded")]
    [InlineData("Inactive")]
    public void Every_category_is_accepted(string c) => Assert.Contains(c, EngagementCategories.All);

    [Fact]
    public void Prospects_and_warm_leads_stay_in_the_attention_list_while_ended_and_conflict_clients_do_not()
    {
        Assert.DoesNotContain("Prospect", EngagementCategories.QuietCategories);
        Assert.DoesNotContain("WarmLead", EngagementCategories.QuietCategories);
        Assert.Contains("Conflict", EngagementCategories.QuietCategories);
        Assert.Contains("RelationshipEnded", EngagementCategories.QuietCategories);
        Assert.Contains("Reactive", EngagementCategories.QuietCategories);
    }
}
