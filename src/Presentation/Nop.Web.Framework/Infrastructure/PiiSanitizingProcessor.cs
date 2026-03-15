using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenTelemetry;

namespace Nop.Web.Framework.Infrastructure;

/// <summary>
/// OpenTelemetry processor that strips PII (Personally Identifiable Information)
/// from span attributes before export.
/// 
/// nopCommerce handles real customer data (emails, names, addresses, payment details)
/// in its service layer. Rather than relying on each instrumentation point to remember
/// which fields to exclude, this processor sanitises all spans centrally.
/// </summary>
public partial class PiiSanitizingProcessor : BaseProcessor<Activity>
{
    // Attributes that must never appear in traces
    private static readonly HashSet<string> _blockedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "customer.email",
        "customer.name",
        "customer.first_name",
        "customer.last_name",
        "customer.phone",
        "customer.address",
        "payment.card_number",
        "payment.cvv",
        "payment.expiry",
        "user.email",
        "enduser.id",
        "http.request.header.cookie",
        "http.request.header.authorization"
    };

    // Pattern fragments that indicate sensitive attributes
    private static readonly string[] _blockedPatterns =
    [
        "password",
        "token",
        "secret",
        "creditcard",
        "credit_card",
        "ssn",
        "social_security"
    ];

    // Regex to detect email-like patterns in attribute values
    [GeneratedRegex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled)]
    private static partial Regex EmailPattern();

    public override void OnEnd(Activity data)
    {
        if (data == null) return;

        var tagsToUpdate = new List<KeyValuePair<string, object>>();

        foreach (var tag in data.TagObjects)
        {
            // Remove blocked attributes entirely
            if (_blockedAttributes.Contains(tag.Key))
            {
                tagsToUpdate.Add(new KeyValuePair<string, object>(tag.Key, "[REDACTED]"));
                continue;
            }

            // Remove attributes matching blocked patterns
            var keyLower = tag.Key.ToLowerInvariant();
            if (_blockedPatterns.Any(p => keyLower.Contains(p)))
            {
                tagsToUpdate.Add(new KeyValuePair<string, object>(tag.Key, "[REDACTED]"));
                continue;
            }

            // Sanitise string values containing email patterns
            if (tag.Value is string strValue && EmailPattern().IsMatch(strValue))
            {
                var sanitised = EmailPattern().Replace(strValue, "[EMAIL_REDACTED]");
                tagsToUpdate.Add(new KeyValuePair<string, object>(tag.Key, sanitised));
            }
        }

        // Apply sanitisation
        foreach (var tag in tagsToUpdate)
        {
            data.SetTag(tag.Key, tag.Value);
        }

        base.OnEnd(data);
    }
}
