// Copied from jengo-components dotnet/mcp-connector (commit aac7103, 2026-09-25), unchanged except this header.
// Keep in sync: when a NuGet/project reference replaces the copy, delete this folder.
using Microsoft.Extensions.DependencyInjection;

namespace JengoComponents.McpConnector;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the MCP connector core: options + the in-memory OAuth state.
    /// Register your <see cref="IMcpTokenValidator"/> separately (either
    /// <see cref="HmacTokenService"/> or a <see cref="DelegatingTokenValidator"/>
    /// wrapping your API-key lookup).
    /// </summary>
    public static IServiceCollection AddMcpConnector(
        this IServiceCollection services, Action<McpConnectorOptions>? configure = null)
    {
        var options = new McpConnectorOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<OAuthService>();
        return services;
    }
}
