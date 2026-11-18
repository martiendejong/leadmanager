// Copied from jengo-components dotnet/mcp-connector (commit aac7103, 2026-09-25), unchanged except this header.
// Keep in sync: when a NuGet/project reference replaces the copy, delete this folder.
using Microsoft.AspNetCore.Http;

namespace JengoComponents.McpConnector;

public static class RequestOrigin
{
    /// <summary>
    /// The public origin of this request, honoring X-Forwarded-Host / X-Forwarded-Proto
    /// injected by a fronting reverse proxy (IIS/nginx). Discovery documents and the
    /// WWW-Authenticate resource_metadata pointer must be built from THIS, not from the
    /// internal host, or ChatGPT will probe the wrong base URL.
    ///
    /// TRUST REQUIREMENT: these headers are only safe when a trusted proxy sets (and
    /// overwrites) them. If the app is directly exposed, strip X-Forwarded-* at the
    /// edge or clients can reflect an arbitrary host into the discovery documents and
    /// the WWW-Authenticate pointer.
    /// </summary>
    public static string GetPublicOrigin(this HttpRequest request)
    {
        var host = request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? request.Host.Value ?? "";
        var scheme = request.Headers["X-Forwarded-Proto"].FirstOrDefault()
                     ?? (host.StartsWith("localhost") || host.StartsWith("127.0.0.1") ? "http" : "https");
        return $"{scheme}://{host}";
    }
}
