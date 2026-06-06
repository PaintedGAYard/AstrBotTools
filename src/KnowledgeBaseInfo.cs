using System.Management.Automation;

namespace AstrBotTools;

/// <summary>
/// Converts JSON knowledge-base entries from the AstrBot API into <see cref="PSObject"/> instances.
/// Each PSObject is tagged with <c>AstrBotTools.KnowledgeBaseInfo</c> as its type name,
/// matching the formatting rules defined in <c>AstrBotTools.format.ps1xml</c>.
/// </summary>
public static class KnowledgeBaseInfo
{
    /// <summary>
    /// Converts each JSON element in the array to a <see cref="PSObject"/> with all
    /// JSON properties flattened as <see cref="PSNoteProperty"/> entries.
    /// </summary>
    /// <param name="elements">Enumeration of JSON elements from the API response.</param>
    /// <returns>A sequence of PSObjects suitable for <c>WriteObject</c>.</returns>
    public static IEnumerable<PSObject> FromJsonArray(
        IEnumerable<System.Text.Json.JsonElement> elements)
    {
        foreach (var elem in elements)
        {
            var psObj = new PSObject();
            psObj.TypeNames.Insert(0, "AstrBotTools.KnowledgeBaseInfo");

            foreach (var prop in elem.EnumerateObject())
            {
                object? value = prop.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => prop.Value.GetString(),
                    System.Text.Json.JsonValueKind.Number => prop.Value.GetRawText(),
                    System.Text.Json.JsonValueKind.True    => true,
                    System.Text.Json.JsonValueKind.False   => false,
                    System.Text.Json.JsonValueKind.Null    => null,
                    _ => prop.Value.GetRawText()
                };
                psObj.Properties.Add(new PSNoteProperty(prop.Name, value));
            }

            yield return psObj;
        }
    }
}
