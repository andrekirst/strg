using System.Net.WebSockets;
using FluentAssertions;
using Strg.Integration.Tests.Auth;
using Xunit;

namespace Strg.Integration.Tests.Subscriptions;

/// <summary>
/// STRG-066 TC-003 — pins that an unauthenticated WebSocket upgrade to <c>/graphql</c> is
/// rejected by the ASP.NET Core authorization pipeline before the GraphQL subscription layer
/// is ever reached. The defense-in-depth here is the app-wide <c>FallbackPolicy</c> defined in
/// <c>Program.cs</c>:
///
/// <code>
/// options.FallbackPolicy = new AuthorizationPolicyBuilder()
///     .RequireAuthenticatedUser()
///     .Build();
/// </code>
///
/// <para>With no <c>[Authorize]</c> metadata on the <c>MapGraphQL("/graphql")</c> endpoint,
/// the FallbackPolicy is the only thing standing between an unauthenticated WebSocket client
/// and a live subscription. This test drives that path: it initiates a WebSocket upgrade
/// carrying the <c>graphql-transport-ws</c> subprotocol and no <c>Authorization</c> header, and
/// asserts the upgrade is refused. A <see cref="WebSocketException"/> is thrown by
/// <see cref="System.Net.WebSockets.WebSocket.ConnectAsync"/> (or the
/// <c>TestServer</c>-equivalent) when the server returns 401 instead of completing the 101
/// handshake.</para>
///
/// <para><b>Security-finding trap.</b> If this test's <c>ConnectAsync</c> ever <i>succeeds</i>,
/// the fallback policy is not firing on WebSocket upgrades and the subscription surface is
/// open to anonymous clients — a real authorization bypass. The assertion therefore fails
/// <i>loud</i> with an explicit "security finding" message rather than silently recording the
/// current (broken) behavior. Team-lead directive on #108: do not pin a broken state with
/// <c>Should().Be(broken)</c>.</para>
///
/// <para><b>Why Integration.Tests and not GraphQL.Tests.</b> <c>WebApplicationFactory&lt;Program&gt;</c>
/// requires a project reference to <c>Strg.Api</c>. <c>Strg.GraphQl.Tests</c> deliberately
/// omits that reference so its build surface stays decoupled from host-wiring in-flight work
/// (see <c>Strg.GraphQl.Tests.csproj</c>). <c>Strg.Integration.Tests</c> already owns the
/// <c>StrgWebApplicationFactory</c> harness, so this test lives here.</para>
/// </summary>
public sealed class UnauthenticatedSubscriptionRejectionTests(StrgWebApplicationFactory factory)
    : IClassFixture<StrgWebApplicationFactory>
{
    [Fact]
    public async Task Unauthenticated_WebSocket_upgrade_to_graphql_is_rejected_by_fallback_policy()
    {
        var wsClient = factory.Server.CreateWebSocketClient();
        // Subprotocol is what a real graphql-over-ws client sends. Presence or absence of the
        // subprotocol MUST NOT change whether anonymous access is allowed — auth gating happens
        // before the GraphQL-specific negotiation. Adding it anyway ensures we're exercising the
        // same upgrade shape a real client would send, so the test fails the moment the fallback
        // policy is bypassed under the real subprotocol.
        wsClient.SubProtocols.Add("graphql-transport-ws");

        var uri = new UriBuilder(factory.Server.BaseAddress) { Scheme = "ws", Path = "/graphql" }.Uri;

        // No Authorization header is configured on wsClient. A rejection manifests as an
        // exception from ConnectAsync — WebSocketException when the server returns 401 instead of
        // upgrading, or InvalidOperationException from the TestServer path when the upgrade
        // handshake fails before protocol switch.
        Exception? connectFailure = null;
        WebSocket? connected = null;
        try
        {
            connected = await wsClient.ConnectAsync(uri, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or HttpRequestException)
        {
            connectFailure = ex;
        }

        if (connected is not null)
        {
            // SECURITY FINDING: the upgrade succeeded without auth. Close the socket so the test
            // run doesn't leak it, then fail loud.
            try
            {
                await connected.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "test-cleanup",
                    CancellationToken.None);
            }
            catch
            {
                // Best-effort close — the assertion below is what matters.
            }
            finally
            {
                connected.Dispose();
            }

            Assert.Fail(
                "SECURITY FINDING: unauthenticated WebSocket upgrade to /graphql succeeded. " +
                "The FallbackPolicy configured in Program.cs (RequireAuthenticatedUser) is NOT " +
                "firing on WebSocket upgrades, so the subscription surface is reachable without " +
                "a Bearer token. Escalate before pinning this test — do NOT rewrite the " +
                "assertion to expect success.");
        }

        connectFailure.Should().NotBeNull(
            "an unauthenticated upgrade must throw rather than complete the handshake — a null " +
            "exception with no WebSocket means ConnectAsync returned a torn-down state, which " +
            "is itself suspicious and should be investigated.");
    }
}
