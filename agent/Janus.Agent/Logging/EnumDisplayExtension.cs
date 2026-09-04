using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace Janus.Agent.Logging;

// Read the [Display(Name = "...")] attribute from an enum value if
// present, falling back to the value's own name. Cached because it's
// called on every log line render.

internal static class EnumDisplayExtensions
{
    private static readonly ConcurrentDictionary<Enum, string> _cache = new();

    public static string ToDisplay(this Enum value)
    {
        return _cache.GetOrAdd(value, static v =>
        {
            FieldInfo? field = v.GetType().GetField(v.ToString());
            DisplayAttribute? attr = field?.GetCustomAttribute<DisplayAttribute>();
            return attr?.Name ?? v.ToString();
        });
    }
}