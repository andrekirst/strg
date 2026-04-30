namespace Strg.Core.Constants;

/// <summary>
/// String constants for the WebDAV-specific HTTP methods that <c>Microsoft.AspNetCore.Http.HttpMethods</c>
/// does not pre-define. Centralising the literals applies the project-wide "no magic strings for
/// HTTP methods" rule to the WebDAV verb surface — a typo in <c>"MKOCL"</c> in the middleware
/// would otherwise drift past any compile-time check and silently route to the 501-fallback or
/// to <c>_next</c>, depending on where the typo lands.
/// </summary>
public static class WebDavMethods
{
    /// <summary>RFC 4918 §9.3 — create a collection.</summary>
    public const string Mkcol = "MKCOL";

    /// <summary>RFC 4918 §9.8 — copy a resource.</summary>
    public const string Copy = "COPY";

    /// <summary>RFC 4918 §9.9 — move/rename a resource.</summary>
    public const string Move = "MOVE";

    /// <summary>RFC 4918 §9.1 — retrieve properties.</summary>
    public const string PropFind = "PROPFIND";

    /// <summary>RFC 4918 §9.2 — modify dead properties.</summary>
    public const string PropPatch = "PROPPATCH";

    /// <summary>RFC 4918 §9.10 — acquire or refresh a lock.</summary>
    public const string Lock = "LOCK";

    /// <summary>RFC 4918 §9.11 — release a lock.</summary>
    public const string Unlock = "UNLOCK";
}
