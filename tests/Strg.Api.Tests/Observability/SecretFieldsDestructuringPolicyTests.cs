using System.IO;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Strg.Infrastructure.Observability;
using Xunit;

namespace Strg.Api.Tests.Observability;

internal sealed class InMemorySink : ILogEventSink
{
    public List<LogEvent> Events { get; } = new();
    public void Emit(LogEvent logEvent) => Events.Add(logEvent);
}

public sealed class SecretFieldsDestructuringPolicyTests
{
    private readonly SecretFieldsDestructuringPolicy _policy = new();
    private readonly PassThroughValueFactory _factory = new();

    [Fact]
    public void TryDestructure_RedactsPasswordProperty()
    {
        var request = new CreateUserPayload("user@example.com", "super-secret-pw");

        var success = _policy.TryDestructure(request, _factory, out var destructured);

        success.Should().BeTrue();
        var structure = destructured.Should().BeOfType<StructureValue>().Subject;
        PropertyText(structure, "Email").Should().Be("\"user@example.com\"");
        PropertyText(structure, "Password").Should().Be("\"***\"");
    }

    [Fact]
    public void TryDestructure_RedactsEveryKnownSecretKeyword()
    {
        var payload = new AllSecretsPayload(
            Password: "p1", PasswordHash: "p2", NewPassword: "p3",
            CurrentPassword: "p4", OldPassword: "p5", ClientSecret: "p6",
            RefreshToken: "p7", AccessToken: "p8", IdToken: "p9",
            BearerToken: "p10", Authorization: "p11", Cookie: "p12",
            ApiKey: "p13", Secret: "p14", PrivateKey: "p15",
            ConnectionString: "p16", SafeField: "visible");

        var success = _policy.TryDestructure(payload, _factory, out var destructured);

        success.Should().BeTrue();
        var structure = destructured.Should().BeOfType<StructureValue>().Subject;
        var secretPropertyNames = new[]
        {
            "Password", "PasswordHash", "NewPassword", "CurrentPassword", "OldPassword",
            "ClientSecret", "RefreshToken", "AccessToken", "IdToken", "BearerToken",
            "Authorization", "Cookie", "ApiKey", "Secret", "PrivateKey", "ConnectionString",
        };
        foreach (var name in secretPropertyNames)
        {
            PropertyText(structure, name).Should().Be("\"***\"", $"'{name}' must be redacted");
        }
        PropertyText(structure, "SafeField").Should().Be("\"visible\"");
    }

    [Fact]
    public void TryDestructure_MatchIsCaseInsensitive()
    {
        var request = new LowercasePasswordPayload("leaked");

        var success = _policy.TryDestructure(request, _factory, out var destructured);

        success.Should().BeTrue();
        var structure = destructured.Should().BeOfType<StructureValue>().Subject;
        PropertyText(structure, "password").Should().Be("\"***\"");
    }

    [Fact]
    public void TryDestructure_RedactsNestedSecretsViaRecursiveFactory()
    {
        // When the pipeline calls TryDestructure on the outer object and recurses via
        // CreatePropertyValue(..., destructureObjects: true), the policy is re-entered for
        // the inner object. Verified end-to-end in LoggerPipeline test below.
        var outer = new OuterWithInner(new CreateUserPayload("a@b.test", "inner-secret"));

        var success = _policy.TryDestructure(outer, _factory, out var destructured);

        success.Should().BeTrue();
        destructured.Should().BeOfType<StructureValue>();
        // Direct TryDestructure doesn't recurse through the policy itself — the factory is
        // responsible for that. End-to-end coverage lives in LoggerPipeline_* tests.
    }

    [Fact]
    public void TryDestructure_LeavesBclTypesToDefaultHandlers()
    {
        var success = _policy.TryDestructure(new Uri("https://example.test/"), _factory, out _);

        success.Should().BeFalse();
    }

    [Fact]
    public void TryDestructure_LeavesPrimitivesToDefaultHandlers()
    {
        _policy.TryDestructure(42, _factory, out _).Should().BeFalse();
        _policy.TryDestructure("hello", _factory, out _).Should().BeFalse();
        _policy.TryDestructure(DayOfWeek.Monday, _factory, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDestructure_LeavesCollectionsToDefaultHandlers()
    {
        var list = new List<string> { "a", "b" };

        _policy.TryDestructure(list, _factory, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDestructure_RedactsStringKeyedDictionaryValuesByKey()
    {
        // OpenIddictRequest and any Dictionary<string, T> hit this path. The dict check runs
        // before the BCL namespace short-circuit precisely so Dictionary<string,T> (which
        // lives under System.Collections.Generic) gets inspected key-by-key.
        var dict = new Dictionary<string, string>
        {
            ["Password"] = "pw",
            ["client_secret"] = "cs",
            ["refresh_token"] = "rt",
            ["code_verifier"] = "cv",
            ["safe"] = "visible",
        };

        var success = _policy.TryDestructure(dict, _factory, out var destructured);

        success.Should().BeTrue();
        var dictionary = destructured.Should().BeOfType<DictionaryValue>().Subject;
        DictEntryText(dictionary, "Password").Should().Be("\"***\"");
        DictEntryText(dictionary, "client_secret").Should().Be("\"***\"");
        DictEntryText(dictionary, "refresh_token").Should().Be("\"***\"");
        DictEntryText(dictionary, "code_verifier").Should().Be("\"***\"");
        DictEntryText(dictionary, "safe").Should().Be("\"visible\"");
    }

    [Fact]
    public void TryDestructure_DictionaryKeyMatchIsCaseInsensitive()
    {
        var dict = new Dictionary<string, string>
        {
            ["PASSWORD"] = "leaked-uppercase",
            ["Client_Secret"] = "leaked-mixedcase",
        };

        var success = _policy.TryDestructure(dict, _factory, out var destructured);

        success.Should().BeTrue();
        var dictionary = destructured.Should().BeOfType<DictionaryValue>().Subject;
        DictEntryText(dictionary, "PASSWORD").Should().Be("\"***\"");
        DictEntryText(dictionary, "Client_Secret").Should().Be("\"***\"");
    }

    [Fact]
    public void TryDestructure_RedactsReadOnlyDictionaryOnlyShape()
    {
        // OpenIddictMessage implements IReadOnlyDictionary<string, _> but NOT the non-generic
        // IDictionary. The policy therefore detects via IEnumerable<KeyValuePair<string, _>>
        // which catches both — pinning this as regression coverage so a future refactor back
        // to `value is IDictionary` doesn't silently reopen the leak on OpenIddictRequest.
        var message = new ReadOnlyDictionaryOnlyMessage(new Dictionary<string, string>
        {
            ["password"] = "leaked",
            ["safe"] = "visible",
        });

        var success = _policy.TryDestructure(message, _factory, out var destructured);

        success.Should().BeTrue();
        var dictionary = destructured.Should().BeOfType<DictionaryValue>().Subject;
        DictEntryText(dictionary, "password").Should().Be("\"***\"");
        DictEntryText(dictionary, "safe").Should().Be("\"visible\"");
    }

    [Fact]
    public void TryDestructure_RedactsKekFamilyAndCodeVerifier()
    {
        // Tranche-4 key-material names + PKCE code_verifier / client_assertion. Pinned here
        // so a reordering of the allow-list can't silently drop one.
        var payload = new KeyMaterialPayload(
            Dek: "d", Kek: "k",
            DataEncryptionKey: "dek2", KeyEncryptionKey: "kek2",
            MasterKey: "mk", WrappedKey: "wk",
            CodeVerifier: "pkce", ClientAssertion: "assert");

        var success = _policy.TryDestructure(payload, _factory, out var destructured);

        success.Should().BeTrue();
        var structure = destructured.Should().BeOfType<StructureValue>().Subject;
        foreach (var name in new[]
        {
            "Dek", "Kek", "DataEncryptionKey", "KeyEncryptionKey",
            "MasterKey", "WrappedKey", "CodeVerifier", "ClientAssertion",
        })
        {
            PropertyText(structure, name).Should().Be("\"***\"", $"'{name}' must be redacted");
        }
    }

    [Fact]
    public void TryDestructure_EmitsSentinelForPropertyThatThrowsOnGet()
    {
        // A throwing property getter must not take the whole log event down AND must not be
        // hidden from the log structure — the property shows up with a "<getter threw: …>"
        // sentinel so a debugger can still see the field exists.
        var throwing = new ThrowingGetterPayload("visible");

        var success = _policy.TryDestructure(throwing, _factory, out var destructured);

        success.Should().BeTrue();
        var structure = destructured.Should().BeOfType<StructureValue>().Subject;
        PropertyText(structure, nameof(ThrowingGetterPayload.Safe)).Should().Be("\"visible\"");
        // The sentinel reports the root-cause exception type (InvalidOperationException),
        // not the TargetInvocationException wrapper that PropertyInfo.GetValue produces.
        PropertyText(structure, nameof(ThrowingGetterPayload.Boom))
            .Should().Be("\"<getter threw: InvalidOperationException>\"");
    }

    // --- End-to-end through the full Serilog pipeline ------------------------------------

    [Fact]
    public void LoggerPipeline_RedactsPasswordViaDestructuringOperator()
    {
        // Captures the fact that the policy is actually invoked when registered globally,
        // and that the template uses @-destructuring. Without @, Serilog calls ToString().
        var sink = new InMemorySink();
        using var logger = new LoggerConfiguration()
            .Destructure.With<SecretFieldsDestructuringPolicy>()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var payload = new CreateUserPayload("user@example.com", "leaked-password-value");
        logger.Information("Registering {@Payload}", payload);
        logger.Dispose();

        var rendered = RenderProperties(sink);
        rendered.Should().Contain("\"***\"");
        rendered.Should().NotContain("leaked-password-value");
        rendered.Should().Contain("user@example.com");
    }

    [Fact]
    public void LoggerPipeline_RedactsSecretsInNestedObjects()
    {
        var sink = new InMemorySink();
        using var logger = new LoggerConfiguration()
            .Destructure.With<SecretFieldsDestructuringPolicy>()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var outer = new OuterWithInner(new CreateUserPayload("a@b.test", "inner-secret-must-hide"));
        logger.Information("Outer {@Outer}", outer);
        logger.Dispose();

        var rendered = RenderProperties(sink);
        rendered.Should().NotContain("inner-secret-must-hide");
        rendered.Should().Contain("\"***\"");
    }

    [Fact]
    public void LoggerPipeline_LogContextPushPropertyBypassesPolicy()
    {
        // LogContext.PushProperty emits a ScalarValue directly — it does NOT pass through the
        // destructuring chain. Pinning this as known-and-documented behavior so a future
        // reader doesn't assume PushProperty is safe with credential values.
        var sink = new InMemorySink();
        using var logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Destructure.With<SecretFieldsDestructuringPolicy>()
            .WriteTo.Sink(sink)
            .CreateLogger();

        using (Serilog.Context.LogContext.PushProperty("Password", "bypasses-policy"))
        {
            logger.Information("captured");
        }
        logger.Dispose();

        RenderProperties(sink).Should().Contain("bypasses-policy");
    }

    [Fact]
    public void LoggerPipeline_PreservesPlainStringFormatting()
    {
        // A raw secret passed as a {Message} argument (NOT via a destructured object) is not
        // intercepted — the policy only works on object properties. Documented here so future
        // readers don't assume otherwise: callers must never pass bare secrets as log scalars.
        var sink = new InMemorySink();
        using var logger = new LoggerConfiguration()
            .Destructure.With<SecretFieldsDestructuringPolicy>()
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("Value is {Value}", "not-a-property-still-visible");
        logger.Dispose();

        RenderProperties(sink).Should().Contain("not-a-property-still-visible");
    }

    // --- Helpers -------------------------------------------------------------------------

    private static string PropertyText(StructureValue structure, string propertyName)
    {
        var property = structure.Properties.Single(p => p.Name == propertyName);
        using var writer = new StringWriter();
        property.Value.Render(writer);
        return writer.ToString();
    }

    private static string DictEntryText(DictionaryValue dictionary, string key)
    {
        var entry = dictionary.Elements
            .Single(e => string.Equals((string?)e.Key.Value, key, StringComparison.Ordinal));
        using var writer = new StringWriter();
        entry.Value.Render(writer);
        return writer.ToString();
    }

    private static string RenderProperties(InMemorySink sink)
    {
        using var writer = new StringWriter();
        foreach (var logEvent in sink.Events)
        {
            foreach (var (name, value) in logEvent.Properties)
            {
                writer.Write(name);
                writer.Write('=');
                value.Render(writer);
                writer.Write(';');
            }
            writer.WriteLine();
        }
        return writer.ToString();
    }

    private sealed class PassThroughValueFactory : ILogEventPropertyValueFactory
    {
        public LogEventPropertyValue CreatePropertyValue(object? value, bool destructureObjects = false)
            => value is null ? new ScalarValue(null) : new ScalarValue(value);
    }

    public sealed record CreateUserPayload(string Email, string Password);

    public sealed record LowercasePasswordPayload(string password);

    public sealed record OuterWithInner(CreateUserPayload Inner);

    public sealed record AllSecretsPayload(
        string Password,
        string PasswordHash,
        string NewPassword,
        string CurrentPassword,
        string OldPassword,
        string ClientSecret,
        string RefreshToken,
        string AccessToken,
        string IdToken,
        string BearerToken,
        string Authorization,
        string Cookie,
        string ApiKey,
        string Secret,
        string PrivateKey,
        string ConnectionString,
        string SafeField);

    public sealed class ThrowingGetterPayload(string safe)
    {
        public string Safe { get; } = safe;
        public string Boom => throw new InvalidOperationException("property getter should not crash the log pipeline");
    }

    // Stand-in for OpenIddictMessage: implements IReadOnlyDictionary<string, _> but NOT the
    // non-generic IDictionary, so the policy must detect via the IEnumerable<KVP<string, _>>
    // shape, not the IDictionary cast.
    public sealed class ReadOnlyDictionaryOnlyMessage(IDictionary<string, string> source)
        : IReadOnlyDictionary<string, string>
    {
        private readonly IReadOnlyDictionary<string, string> _inner = new Dictionary<string, string>(source);

        public string this[string key] => _inner[key];
        public IEnumerable<string> Keys => _inner.Keys;
        public IEnumerable<string> Values => _inner.Values;
        public int Count => _inner.Count;
        public bool ContainsKey(string key) => _inner.ContainsKey(key);
        public bool TryGetValue(string key, out string value) => _inner.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed record KeyMaterialPayload(
        string Dek,
        string Kek,
        string DataEncryptionKey,
        string KeyEncryptionKey,
        string MasterKey,
        string WrappedKey,
        string CodeVerifier,
        string ClientAssertion);
}
