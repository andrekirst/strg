using Microsoft.AspNetCore.Http;
using Strg.Core.Constants;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data;

public sealed class HttpCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    public Guid UserId
    {
        get
        {
            var claim = httpContextAccessor.HttpContext?.User.FindFirst(StrgClaimNames.Subject)?.Value;
            return Guid.TryParse(claim, out var id) ? id : Guid.Empty;
        }
    }
}
