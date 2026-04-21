using Microsoft.AspNetCore.Http;
using Strg.Core.Constants;

namespace Strg.Infrastructure.Data;

public sealed class HttpTenantContext(IHttpContextAccessor httpContextAccessor) : ITenantContext
{
    public Guid TenantId
    {
        get
        {
            var claim = httpContextAccessor.HttpContext?.User.FindFirst(StrgClaimNames.TenantId)?.Value;
            return Guid.TryParse(claim, out var id) ? id : Guid.Empty;
        }
    }
}
