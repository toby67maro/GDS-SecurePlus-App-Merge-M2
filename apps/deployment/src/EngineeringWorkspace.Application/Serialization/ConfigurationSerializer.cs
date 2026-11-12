namespace EngineeringWorkspace.Application.Serialization;

/// <summary>A serializable configuration document: sections of ordered key/value pairs.</summary>
public sealed record ConfigurationDocument(IReadOnlyList<ConfigurationDocumentSection> Sections);

public sealed record ConfigurationDocumentSection(string Name, IReadOnlyList<KeyValuePair<string, string?>> Values);

/// <summary>Serializes configuration documents. INI now; JSON/YAML later. No format logic in ViewModels.</summary>
public interface IConfigurationSerializer
{
    string FormatName { get; }
    string FileExtension { get; }
    string Serialize(ConfigurationDocument document);
    ConfigurationDocument Deserialize(string content);
}
