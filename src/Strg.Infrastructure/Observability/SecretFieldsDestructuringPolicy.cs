using System.Collections;
using System.Collections.Frozen;
using System.Reflection;
using Serilog.Core;
using Serilog.Events;

namespace Strg.Infrastructure.Observability;

/// <summary>
/// Serilog destructuring policy that replaces the value of any property or dictionary entry
/// whose name matches a known credential keyword with <c>"***"</c>. Applies to user-defined
/// DTOs passed via the <c>{@obj}</c> destructuring operator — including third-party types we
/// don't own (<c>OpenIddictRequest</c>, dictionary-shaped request bodies, framework DTOs) —
/// so a single registration closes the leak regardless of the call site.
///
/// <para>
/// The match is name-based, case-insensitive, against the CLR property name or the string
/// key on dictionary-like values. Dictionary detection uses the generic
/// <c>IEnumerable&lt;KeyValuePair&lt;string, T&gt;&gt;</c> shape, which catches
/// <c>IDictionary&lt;string, T&gt;</c>, <c>IReadOnlyDictionary&lt;string, T&gt;</c> (including
/// <c>OpenIddictMessage</c>, which does not implement the non-generic <c>IDictionary</c>),
/// and custom collections that expose the same shape. Both PascalCase CLR names and
/// snake_case OAuth/OIDC wire names live in the allow-list so the policy catches DTOs and raw
/// request dictionaries alike. False positives (a non-secret property genuinely named
/// <c>"Password"</c>) are an accepted trade: logging <c>"***"</c> for a harmless value is
/// cheap; leaking a real credential is not.
/// </para>
///
/// <para>
/// <b>Known coverage limits — NOT redacted:</b>
/// <list type="bullet">
/// <item><description>Bare scalar template arguments: <c>logger.Information("tok={T}", raw)</c>.
/// The policy only fires on object/dictionary destructuring. Never pass a raw credential as
/// a scalar template argument.</description></item>
/// <item><description><c>LogContext.PushProperty("Password", raw)</c> injects a
/// <c>ScalarValue</c> directly and bypasses the destructuring chain entirely.</description></item>
/// <item><description>BCL/framework types (<c>System.*</c>, <c>Microsoft.*</c>) flow to
/// Serilog's default handlers <i>except</i> string-keyed dictionaries, which are inspected
/// here. If a framework DTO is logged via <c>{@...}</c>, project to a safe application DTO
/// first.</description></item>
/// <item><description>Public <i>fields</i> (not properties) are not surfaced by reflection
/// here. Use auto-properties on DTOs that could carry credentials.</description></item>
/// <item><description>Beyond Serilog's <c>MaximumDestructuringDepth</c> (default 10),
/// truncated leaves render via <c>ToString()</c> — record types whose auto-generated
/// <c>ToString()</c> includes credential-named properties can leak at cycle depth. Avoid
/// cyclic graphs in DTOs that carry credentials, or override <c>ToString()</c> on those
/// records.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class SecretFieldsDestructuringPolicy : IDestructuringPolicy
{
    private const string Redaction = "***";

    // Ordinal-ignore-case match on the property's declared name (or a dictionary key). Keep
    // both PascalCase CLR names and snake_case OAuth/OIDC wire names — case-insensitive lookup
    // bridges case, but NOT underscores, so "ClientSecret" and "client_secret" are separate
    // entries. Extend with operational discretion — false positives are cheap, false negatives
    // are credential leaks.
    private static readonly FrozenSet<string> SecretPropertyNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // CLR / record property names
            "Password",
            "PasswordHash",
            "NewPassword",
            "CurrentPassword",
            "OldPassword",
            "ClientSecret",
            "RefreshToken",
            "AccessToken",
            "IdToken",
            "BearerToken",
            "Authorization",
            "Cookie",
            "ApiKey",
            "Secret",
            "PrivateKey",
            "ConnectionString",
            "CodeVerifier", // PKCE proof — bearer-equivalent during code exchange
            "ClientAssertion",
            "Assertion",

            // File-encryption key material (Tranche-4 prep: FileKey entity, DEK/KEK wrapping).
            // Listed proactively so a leak can't sneak in when STRG-026+ lands.
            "Dek",
            "Kek",
            "EncryptedDek",
            "DataKey",
            "DataEncryptionKey",
            "KeyEncryptionKey",
            "MasterKey",
            "WrappedKey",

            // Snake-case wire names — OpenIddictRequest / form-encoded bodies / JSON wire
            // payloads use these directly, and the dictionary path below routes string keys
            // through this same allow-list.
            "password",
            "client_secret",
            "refresh_token",
            "access_token",
            "id_token",
            "bearer_token",
            "api_key",
            "private_key",
            "connection_string",
            "code_verifier",
            "client_assertion",
            "assertion",
            "encrypted_dek",
            "data_key",
            "data_encryption_key",
            "key_encryption_key",
            "master_key",
            "wrapped_key",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public bool TryDestructure(
        object value,
        ILogEventPropertyValueFactory propertyValueFactory,
        out LogEventPropertyValue result)
    {
        if (value is null)
        {
            result = null!;
            return false;
        }

        var type = value.GetType();

        // Fast path: primitives, enums, and strings are always scalars.
        if (type.IsPrimitive || type.IsEnum || value is string)
        {
            result = null!;
            return false;
        }

        // String-keyed dictionaries must be inspected key-by-key. Serilog's default dictionary
        // handler renders every value verbatim, which leaks credentials like "password" /
        // "client_secret" / "code_verifier" when someone logs a raw OpenIddictRequest or any
        // Dictionary<string, T>. This runs BEFORE the BCL short-circuit because
        // Dictionary<string, T> lives under System.Collections.Generic, and uses the generic
        // IEnumerable<KeyValuePair<string, _>> shape so it also catches
        // IReadOnlyDictionary<string, _>-only types like OpenIddictMessage.
        if (TryDestructureStringKeyedDictionary(value, type, propertyValueFactory, out result))
        {
            return true;
        }

        // Leave BCL and framework types to Serilog's built-in handlers. This sidesteps
        // special-cased scalars (DateTime, Guid, Uri, TimeSpan) and non-dictionary collections
        // (List<T>, T[], etc.). Framework types destructured via @ are NOT redacted here —
        // project to a safe application DTO first.
        if (type.Namespace is { } ns &&
            (ns.StartsWith("System", StringComparison.Ordinal) ||
             ns.StartsWith("Microsoft", StringComparison.Ordinal)))
        {
            result = null!;
            return false;
        }

        // Non-dictionary user-defined collections: let the default sequence handler render them.
        if (value is IEnumerable)
        {
            result = null!;
            return false;
        }

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var destructured = new List<LogEventProperty>(properties.Length);

        foreach (var property in properties)
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            LogEventPropertyValue propertyValue;
            if (SecretPropertyNames.Contains(property.Name))
            {
                propertyValue = new ScalarValue(Redaction);
            }
            else
            {
                object? rawValue;
                try
                {
                    rawValue = property.GetValue(value);
                }
                catch (Exception ex)
                {
                    // A throwing property getter (e.g. X509Certificate2.HasPrivateKey on a
                    // bundle without one) must not take the whole log event down. Emit a
                    // sentinel with the exception type so the property still appears in the
                    // log structure — silent skip would hide the existence of the field from
                    // a debugger trying to understand the DTO's shape. Unwrap the
                    // TargetInvocationException that PropertyInfo.GetValue always wraps the
                    // real exception in, so the sentinel reports the actual root-cause type.
                    var rootCause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                    propertyValue = new ScalarValue($"<getter threw: {rootCause.GetType().Name}>");
                    destructured.Add(new LogEventProperty(property.Name, propertyValue));
                    continue;
                }

                // destructureObjects: true so nested DTOs recurse back through this policy
                // and their secret properties are redacted too.
                propertyValue = propertyValueFactory.CreatePropertyValue(rawValue, destructureObjects: true);
            }

            destructured.Add(new LogEventProperty(property.Name, propertyValue));
        }

        result = new StructureValue(destructured, type.Name);
        return true;
    }

    private static bool TryDestructureStringKeyedDictionary(
        object value,
        Type type,
        ILogEventPropertyValueFactory propertyValueFactory,
        out LogEventPropertyValue result)
    {
        Type? pairType = null;
        foreach (var @interface in type.GetInterfaces())
        {
            if (!@interface.IsGenericType || @interface.GetGenericTypeDefinition() != typeof(IEnumerable<>))
            {
                continue;
            }

            var candidate = @interface.GetGenericArguments()[0];
            if (candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(KeyValuePair<,>) &&
                candidate.GetGenericArguments()[0] == typeof(string))
            {
                pairType = candidate;
                break;
            }
        }

        if (pairType is null)
        {
            result = null!;
            return false;
        }

        var keyProperty = pairType.GetProperty("Key")!;
        var valueProperty = pairType.GetProperty("Value")!;

        var elements = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>();
        foreach (var entry in (IEnumerable)value)
        {
            if (entry is null)
            {
                continue;
            }

            var key = (string?)keyProperty.GetValue(entry);
            if (key is null)
            {
                continue;
            }

            LogEventPropertyValue entryValue;
            if (SecretPropertyNames.Contains(key))
            {
                entryValue = new ScalarValue(Redaction);
            }
            else
            {
                object? rawValue;
                try
                {
                    rawValue = valueProperty.GetValue(entry);
                }
                catch
                {
                    // A throwing pair-value getter must not nuke the whole log event. Skip
                    // this entry and keep going.
                    continue;
                }

                entryValue = propertyValueFactory.CreatePropertyValue(rawValue, destructureObjects: true);
            }

            elements.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(new ScalarValue(key), entryValue));
        }

        result = new DictionaryValue(elements);
        return true;
    }
}
