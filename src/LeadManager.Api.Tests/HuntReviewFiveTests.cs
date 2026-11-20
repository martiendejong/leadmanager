using System.Reflection;
using LeadManager.Api.Controllers;
using LeadManager.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace LeadManager.Api.Tests;

/// <summary>The fifth independent review of the lead hunt (PR 66).</summary>
public class HuntApiKeyGuardTests
{
    private static readonly string[] ReadActions =
        { nameof(HuntController.Get), nameof(HuntController.Recent), nameof(HuntController.Evidence), nameof(HuntController.Capabilities), nameof(HuntController.Products) };
    private static readonly string[] WriteActions =
        { nameof(HuntController.Start), nameof(HuntController.ReviewByAgent), nameof(HuntController.VerifyMany), nameof(HuntController.VerifyOne), nameof(HuntController.Cancel) };

    private static IEnumerable<MethodInfo> Actions() =>
        typeof(HuntController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any());

    /// <summary>The same rule as the middleware in Program.cs: a key passes only with AllowApiKey and without DenyApiKey, class and action together.</summary>
    private static bool KeyMayCall(MethodInfo m) =>
        (m.GetCustomAttribute<AllowApiKeyAttribute>() != null || typeof(HuntController).GetCustomAttribute<AllowApiKeyAttribute>() != null)
        && m.GetCustomAttribute<DenyApiKeyAttribute>() == null;

    [Fact]
    public void The_hunt_controller_does_not_allow_keys_on_the_whole_class()
    {
        Assert.Null(typeof(HuntController).GetCustomAttribute<AllowApiKeyAttribute>(inherit: true));
        Assert.NotNull(typeof(HuntController).GetCustomAttribute<AuthorizeAttribute>());
    }

    [Theory]
    [InlineData(nameof(HuntController.Get))]
    [InlineData(nameof(HuntController.Recent))]
    [InlineData(nameof(HuntController.Evidence))]
    [InlineData(nameof(HuntController.Capabilities))]
    [InlineData(nameof(HuntController.Products))]
    public void A_key_may_read(string action) => Assert.True(KeyMayCall(typeof(HuntController).GetMethod(action)!));

    [Theory]
    [InlineData(nameof(HuntController.Start))]
    [InlineData(nameof(HuntController.ReviewByAgent))]
    [InlineData(nameof(HuntController.VerifyMany))]
    [InlineData(nameof(HuntController.VerifyOne))]
    [InlineData(nameof(HuntController.Cancel))]
    public void A_key_may_not_start_review_verify_or_cancel(string action)
    {
        var m = typeof(HuntController).GetMethod(action)!;
        Assert.NotNull(m.GetCustomAttribute<DenyApiKeyAttribute>());
        Assert.False(KeyMayCall(m));
    }

    [Fact]
    public void Every_action_of_the_hunt_controller_is_classified_so_a_new_one_cannot_slip_through_open()
    {
        var names = Actions().Select(m => m.Name).ToHashSet();
        Assert.Equal(ReadActions.Concat(WriteActions).OrderBy(x => x), names.OrderBy(x => x));
        foreach (var m in Actions()) Assert.Equal(ReadActions.Contains(m.Name), KeyMayCall(m));
    }

    [Fact]
    public void The_agent_endpoints_are_not_reachable_with_a_key_at_all()
    {
        Assert.NotNull(typeof(HuntAgentController).GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Null(typeof(HuntAgentController).GetCustomAttribute<AllowApiKeyAttribute>(inherit: true));
    }
}
