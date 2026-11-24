using System.Text.Json.Nodes;
using QControl.Core.Data;

namespace QControl.Core.Model;

/// <summary>
/// 出厂默认 presets / 出厂基准兜底。
///
/// ⚠️ 类名是 PresetDefaults 而不是 Presets：`ModelStore` 有一个同名实例属性
///    `Presets`（当前配置树），同命名空间下会互相遮蔽 —— 第一次编译就撞了，
///    `Presets.CloneDefaults()` 被解析成了对属性的成员访问。
///
/// 数据源是一份 200 行嵌套字面量的默认配置，已搬成 json 入库（不再手改代码）。
/// 用 <see cref="JsonNode"/> 而不是生成几百个 C# 属性，是因为早先实现的 Model
/// 本身就是「JSON 树 + 按 path 读写」—— 平移成 JsonNode 才是忠实的，生成强类型
/// 反而要在 get/set/reset 时再做一层映射。
/// </summary>
public static class PresetDefaults
{
    private static readonly Lazy<JsonObject> DefaultsNode =
        new(() => EmbeddedData.ReadObject("presets.default.json"));

    private static readonly Lazy<JsonObject> FallbackNode =
        new(() => EmbeddedData.ReadObject("presets.factory-fallback.json"));

    /// <summary>出厂默认（**只读**：调用方要改必须自己 <see cref="CloneDefaults"/>）。</summary>
    public static JsonObject Defaults => DefaultsNode.Value;

    /// <summary>出厂基准探测失败时的兜底（注册表实测值，非编造）。</summary>
    public static JsonObject FactoryFallback => FallbackNode.Value;

    /// <summary>深拷贝一份默认 presets —— 新建 model.json 时用它。</summary>
    public static JsonObject CloneDefaults() => (JsonObject)DefaultsNode.Value.DeepClone();

    /// <summary>配置节名（默认配置的第一级键）。</summary>
    public static IReadOnlyList<string> Sections
        => DefaultsNode.Value.Select(kv => kv.Key).ToList();

    public static bool HasSection(string name) => DefaultsNode.Value.ContainsKey(name);

    /// <summary>
    /// 深合并：<paramref name="overrideNode"/> 的叶子覆盖 base，对象递归合并。
    /// 深合并 —— 机型档案的 presets_overrides 靠它注入。
    /// </summary>
    public static void DeepMerge(JsonObject baseNode, JsonObject overrideNode)
    {
        foreach (var (key, value) in overrideNode.ToList())
        {
            var existing = baseNode[key];
            if (existing is JsonObject baseObj && value is JsonObject overObj)
            {
                // 两边都是对象 ⇒ 递归；注意不能直接用 existing 的引用，
                // 它已挂在 baseNode 上，in-place 改就是在改 base。
                DeepMerge(baseObj, overObj);
            }
            else
            {
                baseNode[key] = value?.DeepClone();
            }
        }
    }
}
