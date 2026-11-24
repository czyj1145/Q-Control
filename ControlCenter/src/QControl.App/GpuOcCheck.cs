using QControl.Hal.Nvml;

namespace QControl.App;

/// <summary>
/// NVML 超频自检（`--gpu-oc-selftest`）。
///
/// 只走很温和的值（核心 +50 / 显存 +100 有效 MHz），跑完**一定复位**。
/// 每一步都先打印再执行 —— 万一 NVML 里原生崩了（访问冲突不产生 .NET 异常，
/// 进程直接没），屏幕上最后一行就是崩在哪一步。
/// </summary>
internal static class GpuOcCheck
{
    public static int Run()
    {
        Console.WriteLine(new string('=', 66));
        Console.WriteLine(" GPU 超频自检（NVML 时钟偏移）");
        Console.WriteLine(new string('=', 66));

        var nvml = NvmlOc.Instance;

        Console.WriteLine();
        Console.WriteLine("[1] 加载");
        var (ok, err) = nvml.Available();
        Console.WriteLine($"  available = {ok}" + (err is null ? "" : $"   错误：{err}"));
        if (!ok) return 1;

        Console.WriteLine();
        Console.WriteLine("[2] 能力（范围 + 当前偏移）");
        var cap = nvml.Capability(refresh: true);
        Console.WriteLine("  " + cap.ToJsonString());

        Console.WriteLine();
        Console.WriteLine("[3] 实时状态");
        Console.WriteLine("  " + nvml.Status().ToJsonString());

        Console.WriteLine();
        Console.WriteLine("[4] 写自检：核心 +50 / 显存 +100（有效 MHz）…");
        var applied = nvml.Apply(50, 200);   // 显存 raw = 有效 ×2
        Console.WriteLine("  " + applied.ToJsonString());

        Console.WriteLine();
        Console.WriteLine("[5] 复位（两个域归零）…");
        var reset = nvml.Reset();
        Console.WriteLine("  " + reset.ToJsonString());

        Console.WriteLine();
        Console.WriteLine("[6] 复位后能力");
        Console.WriteLine("  " + nvml.Capability(refresh: true).ToJsonString());

        var okAll = applied["ok"]?.GetValue<bool>() == true && reset["ok"]?.GetValue<bool>() == true;
        Console.WriteLine();
        Console.WriteLine(new string('=', 66));
        Console.WriteLine(okAll ? " => 通过" : " => 不通过（看上面的 error：4=没权限，3=不支持）");
        Console.WriteLine(new string('=', 66));
        return okAll ? 0 : 1;
    }
}
