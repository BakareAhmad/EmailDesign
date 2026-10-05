using System.Text.Json;
using DeChimex.Contracts.Orders;

namespace DeChimex.Application.Features.Orders;

/// <summary>
/// Server-side enforcement of the custom order form's required answers. The order form
/// renders required markers client-side, but a customer can bypass the browser; this is
/// the authoritative check (used by the submit handler and pre-checked by the controller
/// so no files are uploaded for an invalid request).
/// </summary>
public static class CustomOrderValidation
{
    /// <summary>
    /// Returns the question text of required options whose answer is missing/empty.
    /// A conditional option (has a parent) is only required when the parent's submitted
    /// answer equals <c>ShowWhenValue</c>, mirroring the form's client-side visibility.
    /// </summary>
    public static IReadOnlyList<string> MissingRequiredQuestions(
        IEnumerable<CategoryOptionDto> options,
        IReadOnlyList<OrderRequirementInput> responses,
        IReadOnlySet<Guid>? pendingFileOptionIds = null)
    {
        var byId = responses
            .Where(r => r.CategoryOptionId is not null)
            .GroupBy(r => r.CategoryOptionId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var missing = new List<string>();
        foreach (var o in options)
        {
            if (!o.IsRequired) continue;

            // Conditional option: only required when its parent's answer matches.
            if (o.ParentOptionId is Guid parentId && !string.IsNullOrWhiteSpace(o.ShowWhenValue))
            {
                var parentAnswered = byId.TryGetValue(parentId, out var parentResponses)
                    && ContainsAnswer(parentResponses, o.ShowWhenValue);
                if (!parentAnswered) continue;
            }

            var answered = o.Type == "FileUpload"
                ? (pendingFileOptionIds is not null && pendingFileOptionIds.Contains(o.Id))
                    || (byId.TryGetValue(o.Id, out var fs) && fs.Any(r => IsNonEmpty(r.Value)))
                : byId.TryGetValue(o.Id, out var list) && list.Any(r => IsNonEmpty(r.Value));

            if (!answered)
                missing.Add(o.Question);
        }
        return missing;
    }

    /// <summary>Whether a captured value is a meaningful answer (choice arrays count when non-empty).</summary>
    public static bool IsNonEmpty(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value == "[]") return false;
        if (value.StartsWith('['))
        {
            try
            {
                var arr = JsonSerializer.Deserialize<string[]>(value);
                return arr is { Length: > 0 } && arr.Any(v => !string.IsNullOrWhiteSpace(v));
            }
            catch
            {
                return true; // not JSON after all; treat the raw text as an answer
            }
        }
        return true;
    }

    private static bool ContainsAnswer(IReadOnlyList<OrderRequirementInput> responses, string want)
    {
        foreach (var r in responses)
        {
            if (r.Value is { } json && json.StartsWith('['))
            {
                try
                {
                    var arr = JsonSerializer.Deserialize<string[]>(json);
                    if (arr is not null && arr.Any(x => string.Equals(x, want, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
                catch
                {
                    // not JSON - fall through
                }
            }
            if (string.Equals(r.Value?.Trim(), want, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
