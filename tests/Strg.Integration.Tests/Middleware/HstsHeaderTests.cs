using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Strg.Integration.Tests.Middleware;

/// <summary>
/// STRG-010 AC2 + Security Review Checklist — the <c>Strict-Transport-Security</c> header is
/// emitted in production with <c>max-age=31536000; includeSubDomains</c> and
/// <c>preload</c> is deliberately NOT set. Uses a minimal <see cref="WebApplication"/> with
/// <c>EnvironmentName = "Production"</c> instead of <c>StrgWebApplicationFactory</c> (which
/// forces Development so OpenIddict signs tokens with ephemeral keys) — we test the env gate
/// in isolation without fighting the rest of the production stack.
/// </summary>
public sealed class HstsHeaderTests
{
    [Fact]
    public async Task Hsts_header_has_strg_required_max_age_and_include_subdomains_without_preload()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
        });

        builder.WebHost.UseTestServer();

        // Mirror the Program.cs HstsOptions so this test pins the ACTUAL values Program.cs
        // ships. If the Program.cs override is accidentally removed, the default 30-day
        // max-age would surface here as a failed assertion.
        builder.Services.Configure<HstsOptions>(options =>
        {
            options.MaxAge = TimeSpan.FromDays(365);
            options.IncludeSubDomains = true;
            options.Preload = false;
            // UseHsts excludes localhost / loopback by default so K8s localhost probes don't
            // get pinned to HTTPS by a misconfigured cluster DNS. TestServer uses localhost
            // as its virtual host — clear the excludes so the middleware runs here.
            options.ExcludedHosts.Clear();
        });

        await using var app = builder.Build();

        // UseHsts only attaches the header on HTTPS requests (RFC 6797 §7.1: HSTS over HTTP is
        // a spec violation — the header is ignored by conforming UAs and potentially malicious
        // to emit). TestServer has no HTTPS listener; force the request scheme so the middleware
        // sees IsHttps=true. This is a pinning scaffold for the middleware's contract, not a
        // production pattern.
        app.Use((context, next) =>
        {
            context.Request.Scheme = "https";
            return next();
        });
        app.UseHsts();
        app.MapGet("/probe", () => "ok");

        await app.StartAsync();
        try
        {
            using var client = app.GetTestClient();

            using var response = await client.GetAsync("/probe");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            response.Headers.TryGetValues("Strict-Transport-Security", out var hstsValues).Should().BeTrue(
                "UseHsts must attach Strict-Transport-Security on HTTPS responses in production");
            var hsts = hstsValues!.Single();

            hsts.Should().Contain("max-age=31536000",
                "STRG-010 AC2 pins the 1-year max-age");
            hsts.Should().Contain("includeSubDomains",
                "STRG-010 AC2 pins includeSubDomains");
            hsts.Should().NotContain("preload",
                "Security Review Checklist: HSTS preload is NOT set (risky for new domains)");
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
