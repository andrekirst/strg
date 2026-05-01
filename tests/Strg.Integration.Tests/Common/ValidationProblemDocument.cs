using System.Text.Json.Serialization;

namespace Strg.Integration.Tests.Common;

/// <summary>
/// STRG-085 — wire-shape of the RFC 7807 <c>ValidationProblemDetails</c> envelope emitted by
/// <c>ValidationProblemDetailsFilter&lt;TRequest&gt;</c>. ASP.NET Core's
/// <c>Results.ValidationProblem</c> serializes <c>type</c>, <c>title</c>, <c>status</c>, and an
/// <c>errors</c> dictionary keyed by camel-cased property name. This record gives integration
/// tests a strongly-typed view so they can assert per-property error messages without parsing
/// raw JSON.
/// </summary>
internal sealed record ValidationProblemDocument
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("status")]
    public int Status { get; init; }

    [JsonPropertyName("errors")]
    public Dictionary<string, string[]>? Errors { get; init; }
}
