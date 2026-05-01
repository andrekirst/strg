using Microsoft.AspNetCore.Http;

namespace Strg.Plugin.Abstractions.Federation;

/// <summary>
/// Federated-protocol bridge — ActivityPub today, additional protocols (Matrix, AT Protocol)
/// possible in v0.2+. <see cref="PublishActivityAsync"/> is the outbound path (host → fediverse);
/// <see cref="ReceiveActivityAsync"/> is the inbound path (fediverse → host). The inbound path
/// receives the raw <see cref="HttpContext"/> so the plugin can verify HTTP signatures, parse
/// the signed body, and write its own response — the host does no per-protocol validation.
/// </summary>
public interface IFederationProvider : IStrgPlugin
{
    /// <summary>Stable lowercase protocol identifier; example: <c>"activitypub"</c>.</summary>
    string Protocol { get; }

    /// <summary>
    /// Publishes an outbound activity to the federation. Implementations are responsible for
    /// signing, addressing, and delivery semantics defined by the protocol.
    /// </summary>
    Task PublishActivityAsync(FederationActivity activity, CancellationToken cancellationToken);

    /// <summary>
    /// Handles an inbound federated request. The plugin owns end-to-end response writing; the
    /// host has already routed the request to the plugin's <see cref="IEndpointModule.MountPath"/>
    /// — typically <c>/plugins/{name}/inbox</c> — and intentionally performs no protocol
    /// validation.
    /// </summary>
    Task ReceiveActivityAsync(HttpContext httpContext, CancellationToken cancellationToken);
}
