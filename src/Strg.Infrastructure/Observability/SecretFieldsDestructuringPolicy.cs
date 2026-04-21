using System.Collections;
using System.Collections.Frozen;
using System.Reflection;
using Serilog.Core;
using Serilog.Events;

namespace Strg.Infrastructure.Observability;

/// <summary>
/// Serilog destructuring policy that replaces the value of any property whose name matches a
/// known credential keyword with <c>"***"</c>. Applies to user-defined DTOs passed to log
/// templates via the <c>{@obj}</c> destructuring operator — including third-party types we
/// don't own (OpenIddict request shapes, framework DTOs) — so a single registration closes
/// the leak regardless of the call site.
///
/// The match is name-based, case-insensitive, on the CLR property name. False positives
/// (a non-secret property genuinely named <c>"Password"</c>) are an accepted trade: logging
/// <c>"***"</c> instead of a harmless value is cheap; leaking a real credential is not.
/// </summary>
public sealed class SecretFieldsDestructuringPolicy : IDestructuringPolicy
{
    private const string Redaction = "***";

    // Ordinal-ignore-case match on the property's declared name. Keep lowercase-normalized
    // entries here; extend with operational discretion — false positives are cheap, false
    // negatives are credential leaks.
    private static readonly FrozenSet<string> SecretPropertyNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
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

        // Leave BCL and framework types to Serilog's built-in handlers. This also sidesteps
        // special-cased scalars (DateTime, Guid, Uri, TimeSpan) that live under System.*
        // and must not be reflected over.
        if (type.Namespace is { } ns &&
            (ns.StartsWith("System", StringComparison.Ordinal) ||
             ns.StartsWith("Microsoft", StringComparison.Ordinal)))
        {
            result = null!;
            return false;
        }

        // Strings, primitives, enums, and collections are never DTOs — let default handlers
        // produce scalar / sequence / dictionary values.
        if (type.IsPrimitive || type.IsEnum || value is string || value is IEnumerable)
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
                catch
                {
                    // A throwing property getter (e.g. X509Certificate2.HasPrivateKey on a
                    // bundle without one) must not take the whole log event down. Skip.
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
}
