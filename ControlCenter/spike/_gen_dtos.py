"""从 tests/fixtures/*.json 生成 C# DTO。

为什么用生成器而不是手写：
  - 手抄 12 个端点、上百个字段，必然漏字段或打错名 —— 而"契约冻结"的字面要求
    就是**字段名一字不改**；
  - fixtures 是真机真实响应，生成结果可复算：重抓 fixture 再跑一遍即可同步。

生成策略（宁保守不臆造）：
  - 所有属性**可空**：后端随时可能整段省略（数据不可用就隐藏，见项目规矩）；
  - int → long?, float → double?, bool → bool?, 字符串 → string?；
  - null 值 / 空数组 ⇒ 采样时没有类型信息 ⇒ 用 JsonElement? 兜住，并加注释标出，
    绝不猜成某个具体类型；
  - 每个属性都带 [JsonPropertyName]，不依赖任何命名策略。

用法：python _gen_dtos.py
"""
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
FIX = os.path.join(HERE, "..", "tests", "fixtures")
OUT = os.path.join(HERE, "..", "src", "QControl.App", "Http", "Contract")

# fixture 文件名 → 根类型名
ROOTS = {
    "status": "StatusDto",
    "hardware": "HardwareDto",
    "history": "HistoryDto",
    "power_plans": "PowerPlansDto",
    "fancurve": "FanCurveStateDto",
    "gpu_info": "GpuInfoDto",
    "gpu_oc": "GpuOcStateDto",
    "app_settings": "AppSettingsDto",
    "vendor_state": "VendorStateDto",
    "osd": "OsdStateDto",
    "device": "DeviceReportDto",
    "model": "ModelViewDto",
    # 「关于」页（2026-10-22 WPF 迁移补）：版本/机型/端口/启动时间 + 日志现状与尾巴。
    # 注意 fixture 里 capture.last 只有在真导出过一段区间时才出现 —— 这个键**必须**
    # 在样本里有一份，否则生成的 DTO 没有 Last，「最近导出」一行永远读不到。
    "app_about": "AboutDto",
    "app_log_usage": "LogUsageDto",
    # 检查更新（2026-10-27）：本机版本 / 有没有新版 / 上次检查时间 / 失败原因。
    # 样本取"有新版本"这一态 —— error 为 null、「latest 恒在」这两种形状都要覆盖到。
    "app_update": "UpdateDto",
}

CS_KEYWORDS = {"event", "object", "string", "params", "default", "operator",
               "checked", "base", "this", "lock", "fixed", "byte", "short",
               "int", "long", "float", "double", "bool", "class", "return"}


def pascal(s: str) -> str:
    parts = [p for p in s.replace("-", "_").split("_") if p]
    out = "".join(p[:1].upper() + p[1:] for p in parts)
    if not out:
        out = "Value"
    if out[0].isdigit():
        out = "N" + out
    if out.lower() in CS_KEYWORDS:
        out += "_"
    return out


def scalar_type(v):
    if isinstance(v, bool):
        return "bool?"
    if isinstance(v, int):
        return "long?"
    if isinstance(v, float):
        return "double?"
    if isinstance(v, str):
        return "string?"
    return None


def shape_sig(v):
    """递归的「结构签名」——只比形状不比值，用来判断数组元素是否同构。

    为什么要它：数组元素常常是**异构**的（hardware.groups[].rows 里只有部分行带
    adv/mono；model.op_log[].detail 有的扁平、有的嵌 args）。按首元素推类型
    必然丢掉后面元素的字段 —— 自检第一次跑就抓到了这两处。

    ⚠️ 数字**不区分 int / float**（2026-10-23）：C# 侧写 JSON 时 54.0 就是 54，
    于是同一条历史曲线里 `gpu_t` 有时是 54、`cpu_p` 有时是 66 有时是 65.4 ——
    按各自的类型名比形状，整段会被判成"异构"而退化成 JsonElement，
    把 HistoryDto 打回无类型。JSON 层面它们都是 number，本来就该算同一种形状
    （具体落到 long? 还是 double?，由 <see cref="merged"/> 按最宽的那个取值定）。
    """
    if isinstance(v, dict):
        return ("d", tuple(sorted((k, shape_sig(x)) for k, x in v.items())))
    if isinstance(v, list):
        return ("l", tuple(sorted(shape_sig(x) for x in v)))
    if isinstance(v, bool):
        return ("s", "bool")
    if isinstance(v, (int, float)):
        return ("s", "num")
    return ("s", type(v).__name__)


def merged(items):
    """把一组同构 dict 折成**一个**样本 dict，用来推每个键的 C# 类型。

    为什么要它：类型只能从"值的样子"猜，而 C# 写出来的整值不带小数点
    （54.0 → 54）。只看首元素就会把 `cpu_p` 定成 long?，下一拍来个 65.4
    直接反序列化失败 —— 那不是"少一层类型"，是运行时崩。

    取值规则（宁宽不窄）：
      · 同一个键只要**任一**元素是 float ⇒ double?（装得下 int 和 float）；
      · null 参与时忽略它，按非 null 的那个定（可空本来就是 `?` 的语义）；
      · 嵌套的 dict / list 递归折，键集合以并集为准。
    """
    out = {}
    for item in items:
        for k, v in item.items():
            if k not in out:
                out[k] = v
                continue
            old = out[k]
            if old is None and v is not None:
                out[k] = v
            elif isinstance(v, float) and isinstance(old, int) and not isinstance(old, bool):
                out[k] = v
            elif isinstance(v, dict) and isinstance(old, dict):
                out[k] = merged([old, v])
            elif isinstance(v, list) and isinstance(old, list):
                dicts = [x for x in old + v if isinstance(x, dict)]
                out[k] = merged(dicts) if dicts and len(dicts) == len(old + v) else (v or old)
    return out


class Gen:
    def __init__(self):
        self.classes = []        # (name, [(json_name, cs_prop, cs_type, comment)])
        self.used = set()

    def unique(self, name):
        if name not in self.used:
            self.used.add(name)
            return name
        i = 2
        while f"{name}{i}" in self.used:
            i += 1
        name = f"{name}{i}"
        self.used.add(name)
        return name

    def emit(self, obj, type_name, path=""):
        """生成一个类，返回类名（可空类型用 `名?` 由调用方加）。"""
        type_name = self.unique(type_name)
        props = []

        for key in sorted(obj.keys()):
            v = obj[key]
            prop = pascal(key)
            t = scalar_type(v)

            if t is not None:
                props.append((key, prop, t, None))
            elif isinstance(v, dict):
                nested = self.emit(v, type_name.removesuffix("Dto") + prop + "Dto",
                                   path + "." + key)
                props.append((key, prop, nested + "?", None))
            elif isinstance(v, list):
                dict_elems = [x for x in v if isinstance(x, dict)]
                # 全部元素结构一致才敢上强类型；否则用 JsonElement 兜住 ——
                # 宁可少一层类型，也不能丢字段（P1 的全部价值就在"字段一字不改"）。
                homogeneous = bool(v) and len(dict_elems) == len(v) and len({shape_sig(x) for x in v}) == 1
                if homogeneous:
                    nested = self.emit(merged(dict_elems),
                                       type_name.removesuffix("Dto") + prop + "ItemDto",
                                       path + "." + key + "[]")
                    props.append((key, prop, f"List<{nested}>?", None))
                elif dict_elems:
                    props.append((key, prop, "JsonElement?",
                                  f"数组元素结构不一致（{len({shape_sig(x) for x in v})} 种形状）"
                                  f" ⇒ 强类型会丢字段，用 JsonElement 兜住"))
                elif v and scalar_type(v[0]) is not None:
                    # 元素不再带 ? —— 列表里的元素可空没有意义
                    props.append((key, prop, f"List<{scalar_type(v[0]).rstrip('?')}>?", None))
                else:
                    props.append((key, prop, "JsonElement?",
                                  "采样时为空数组 ⇒ 元素类型未知，用 JsonElement 兜住"))
            else:  # None
                props.append((key, prop, "JsonElement?",
                              "采样时为 null ⇒ 类型未知，用 JsonElement 兜住"))

        self.classes.append((type_name, props))
        return type_name

    def render(self):
        # 内层类先输出（递归时已按深层先 append？不 —— 这里按名字排序，C# 不介意顺序）
        out = []
        for name, props in sorted(self.classes, key=lambda x: x[0]):
            out.append(f"public sealed class {name}")
            out.append("{")
            for key, prop, t, comment in props:
                if comment:
                    out.append(f"    // {comment}")
                out.append(f'    [JsonPropertyName("{key}")] public {t} {prop} {{ get; init; }}')
            out.append("}")
            out.append("")
        return "\n".join(out)


def main():
    os.makedirs(OUT, exist_ok=True)
    index = []

    for fname, root in ROOTS.items():
        src = os.path.join(FIX, fname + ".json")
        if not os.path.exists(src):
            print(f"!! 缺 fixture: {src}")
            continue
        with open(src, encoding="utf-8") as f:
            data = json.load(f)
        if not isinstance(data, dict):
            print(f"!! {fname} 根不是对象，跳过")
            continue

        g = Gen()
        g.emit(data, root)
        body = g.render()

        cls = root.removesuffix("Dto")
        header = (f"// <auto-generated>\n"
                  f"//   由 ControlCenter/spike/_gen_dtos.py 从 tests/fixtures/{fname}.json 生成。\n"
                  f"//   不要手改 —— 改 fixture 或改生成器，然后重跑。\n"
                  f"//   字段名与 /api/{fname.replace('_', '/')} 的 JSON 逐字一致。\n"
                  f"// </auto-generated>\n"
                  f"#nullable enable\n\n"
                  f"using System.Text.Json;\nusing System.Text.Json.Serialization;\n\n"
                  f"namespace QControl.App.Http.Contract;\n\n")
        dst = os.path.join(OUT, cls + "Dto.cs")
        with open(dst, "w", encoding="utf-8", newline="\n") as f:
            f.write(header + body + "\n")
        n = len(g.classes)
        nf = sum(len(p) for _, p in g.classes)
        print(f"  {cls + 'Dto.cs':<28} {n:>2} 个类  {nf:>3} 个字段  ← {fname}.json")
        index.append((cls + "Dto", f"/api/{fname.replace('_', '/')}", n, nf))

    print(f"\n共 {len(index)} 个端点，{sum(i[2] for i in index)} 个类，"
          f"{sum(i[3] for i in index)} 个字段")


if __name__ == "__main__":
    main()
