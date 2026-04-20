using Microsoft.AspNetCore.Http;

namespace Strg.Infrastructure.Data;

public sealed class HttpTenantContext(IHttpContextAccessor httpContextAccessor) : ITenantContext
{
    public Guid TenantId
    {
        get
        {
            var claim = httpContextAccessor.HttpContext?.User.FindFirst("tenant_id")?.Value;
            return Guid.TryParse(claim, out var id) ? id : Guid.Empty;
        }
    }
}
