using Hangfire.Dashboard;

namespace LeadManager.Api.Services;

// Dashboard filter: permissive in Development, Admin-role required in Production.
// Code review feedback: unauthenticated /hangfire in prod is a risk.
public class HangfireDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    private readonly IWebHostEnvironment _env;

    public HangfireDashboardAuthorizationFilter(IWebHostEnvironment env)
    {
        _env = env;
    }

    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();

        if (_env.IsDevelopment())
            return true;

        var user = httpContext.User;
        return user?.Identity?.IsAuthenticated == true && user.IsInRole("Admin");
    }
}
