using System.Management.Automation;

namespace AstrBotTools;

/// <summary>
/// 知识库信息输出类。
/// 所有 JSON 属性被展开为 PSObject 的属性，
/// 通过 PSTypeName 绑定 format.ps1xml 控制默认显示列。
/// </summary>
public static class KnowledgeBaseInfo
{
    /// <summary>
    /// 将 JSON 数组中的每个元素转换为 PSObject，
    /// 所有字段展平为属性，并打上类型标记用于格式化。
    /// </summary>
    /// <param name="elements">JSON 数组中的元素枚举</param>
    /// <returns>PSObject 枚举</returns>
    public static IEnumerable<PSObject> FromJsonArray(
        IEnumerable<System.Text.Json.JsonElement> elements)
    {
        foreach (var elem in elements)
        {
            var psObj = new PSObject();
            // 插入类型名称，用于 format.ps1xml 匹配
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
