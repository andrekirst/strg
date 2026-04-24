using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Strg.Api.HealthChecks;
using Xunit;

namespace Strg.Integration.Tests.HealthChecks;

/// <summary>
/// Regression pin for the STRG-008 security guarantee: <see cref="SafeHealthCheckResponseWriter"/>
/// must never serialize a check's exception message to the wire, even when a registered check
/// throws (the framework's default behaviour in that case is to set
/// <c>HealthReportEntry.Description = ex.Message</c>).
///
/// <para>This test deliberately registers a hostile check that throws an exception whose message
/// embeds connection-string-shaped substrings (<c>Host=secret-db.example.com</c>,
/// <c>Username=root</c>, <c>Password=hunter2</c>) and asserts that none of those substrings
/// appear in the response body. Without the writer's <c>entry.Value.Exception is null</c> guard
/// in the description-emission branch, this test would fail.</para>
/// </summary>
public sealed class SafeHealthCheckResponseWriterTests : IAsyncLifetime
{
    private const string SecretShapedExceptionMessage =
        "Failed to connect to Host=secret-db.example.com:5432 Username=root Password=hunter2";

    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddHealthChecks()
            .AddCheck<ThrowingHealthCheck>("hostile", tags: ["strg-ready"]);

        _app = builder.Build();

        _app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("strg-ready"),
            ResponseWriter = SafeHealthCheckResponseWriter.WriteAsync,
        });

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Throwing_check_exception_message_must_not_reach_response_body()
    {
        _client.Should().NotBeNull();

        using var response = await _client!.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "a throwing check is mapped to Unhealthy by the framework, which maps to 503");

        var body = await response.Content.ReadAsStringAsync();

        // PRIMARY REGRESSION PIN: every substring of the exception message must be absent. If a
        // future maintainer removes the `entry.Value.Exception is null` guard in
        // SafeHealthCheckResponseWriter.WriteAsync, this assertion fails.
        body.Should().NotContain("secret-db.example.com",
            "the writer must NEVER serialize ex.Message — Npgsql exception messages embed Host=...");
        body.Should().NotContain("Host=",
            "Npgsql connection-string syntax must never appear in the wire response");
        body.Should().NotContain("Username=",
            "Npgsql connection-string syntax must never appear in the wire response");
        body.Should().NotContain("hunter2",
            "exception payload contents must never appear in the wire response");
        body.Should().NotContain("Password=",
            "Npgsql connection-string syntax must never appear in the wire response");

        // The check still surfaces in the JSON envelope with status Unhealthy — operators get
        // the signal, just not the diagnostic detail. The detail belongs in server-side logs.
        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("status").GetString().Should().Be("Unhealthy");
        var hostile = json.GetProperty("checks").EnumerateArray()
            .FirstOrDefault(c => c.GetProperty("name").GetString() == "hostile");
        hostile.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        hostile.GetProperty("status").GetString().Should().Be("Unhealthy");
        hostile.TryGetProperty("description", out _).Should().BeFalse(
            "description must be suppressed when the entry carries an exception, otherwise "
            + "the framework's default `description = ex.Message` would leak through");
    }

    private sealed class ThrowingHealthCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(SecretShapedExceptionMessage);
        }
    }
}
