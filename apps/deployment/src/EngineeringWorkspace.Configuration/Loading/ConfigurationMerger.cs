using System.Globalization;
using EngineeringWorkspace.Configuration.Layering;

namespace EngineeringWorkspace.Configuration.Loading;

/// <summary>One raw value from one layer.</summary>
public sealed record LayerValue(string Key, string Section, string OriginalKey, string? Value);

/// <summary>Values supplied by one layer (file, environment variables, command line or compiled defaults).</summary>
public sealed record LayerData(ConfigurationLayer Layer, string Source, IReadOnlyList<LayerValue> Values);

/// <summary>The effective value of one key and where it came from.</summary>
public sealed record EffectiveValue(
    string Key,
    string? Value,
    ConfigurationLayer Layer,
    string Source,
    string Section,
    string OriginalKey,
    bool IsOverridden,
    string? PreviousSource);

/// <summary>
/// Merges layers in precedence order. Scalars replace. Collections follow <see cref="CollectionMergePolicy"/>:
/// MergeById collections merge key by key under each stable ID (RemoveById is applied when the element is bound);
/// indexed collections (keys with numeric segments) are replaced as a whole by a higher layer unless declared Append.
/// </summary>
public static class ConfigurationMerger
{
    public static IReadOnlyDictionary<string, EffectiveValue> Merge(IEnumerable<LayerData> layers)
    {
        var effective = new Dictionary<string, EffectiveValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in layers.OrderBy(layer => layer.Layer.Order))
        {
            var values = layer.Values;
            var collections = values.Select(value => IndexedCollectionPrefix(value.Key)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var prefix in collections)
            {
                var strategy = CollectionMergePolicy.Collections.TryGetValue(prefix, out var configured) ? configured : CollectionMergeStrategy.Replace;
                var existing = effective.Keys.Where(key => key.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (strategy == CollectionMergeStrategy.Replace)
                {
                    foreach (var key in existing) effective.Remove(key);
                }
                else if (strategy == CollectionMergeStrategy.Append)
                {
                    var offset = existing.Select(key => IndexAfter(prefix, key)).DefaultIfEmpty(-1).Max() + 1;
                    values = values.Select(value => IndexedCollectionPrefix(value.Key) is { } valuePrefix && string.Equals(valuePrefix, prefix, StringComparison.OrdinalIgnoreCase)
                        ? value with { Key = Reindex(prefix, value.Key, offset) }
                        : value).ToArray();
                }
            }

            foreach (var value in values)
            {
                effective.TryGetValue(value.Key, out var previous);
                effective[value.Key] = new EffectiveValue(value.Key, value.Value, layer.Layer, layer.Source, value.Section, value.OriginalKey,
                    previous is not null, previous?.Source);
            }
        }

        return effective;
    }

    /// <summary>Returns the collection path of a key with a numeric segment (for example A:B:0:C gives A:B).</summary>
    private static string? IndexedCollectionPrefix(string key)
    {
        var segments = key.Split(':');
        for (var index = 1; index < segments.Length; index++)
        {
            if (segments[index].Length > 0 && segments[index].All(char.IsAsciiDigit)) return string.Join(':', segments.Take(index));
        }

        return null;
    }

    private static int IndexAfter(string prefix, string key)
    {
        var segment = key[(prefix.Length + 1)..].Split(':')[0];
        return int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ? index : -1;
    }

    private static string Reindex(string prefix, string key, int offset)
    {
        var rest = key[(prefix.Length + 1)..];
        var separator = rest.IndexOf(':', StringComparison.Ordinal);
        var index = int.Parse(separator < 0 ? rest : rest[..separator], CultureInfo.InvariantCulture) + offset;
        return $"{prefix}:{index.ToString(CultureInfo.InvariantCulture)}{(separator < 0 ? string.Empty : rest[separator..])}";
    }
}
