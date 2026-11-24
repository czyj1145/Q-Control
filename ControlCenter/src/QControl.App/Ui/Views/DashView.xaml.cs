using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using QControl.App.Http.Contract;

namespace QControl.App.Ui.Views;

/// <summary>
/// 总览页：每秒把一份 /api/status 快照刷到卡片上。
///
/// 几条"看起来多余"的分支，每一条都挡过一个真实故障，别因为"看起来更简洁"删掉：
///   · 独显不可用时**必须复位**：没写 else 的写法会让显卡卡停在重启前的读数、被当实时值展示；
///   · 缺数据源的行**整行隐藏**（不是 "--"），项目约定：拿不到就不留占位；
///   · 显存那行把容量/占用/百分比/结温拼成一句（结温只有 HWiNFO 给，没有就不拼）。
///
/// ⚠️ 数值一律 Segoe UI + Tabular（样式里设的）：等宽数字，每秒刷新时行宽不跳。
/// ⚠️ 本类**只读**快照，不写任何东西 —— 写操作走各自的卡（散热/性能/设置页）。
/// </summary>
public partial class DashView : UserControl
{
    private readonly KvRow _cpuTemp;
    private readonly KvRow _cpuPower;
    private readonly KvRow _cpuFreq;
    private readonly KvRow _cpuVolt;

    private readonly KvRow _gpuTemp;
    private readonly KvRow _gpuPower;
    private readonly KvRow _gpuClock;
    private readonly KvRow _gpuVram;

    private readonly LocalApi _api;
    private bool _exporting;

    internal DashView(LocalApi api)
    {
        _api = api;
        InitializeComponent();

        // 卡片上那句「最近 N 秒」必须跟着缓冲的时间窗走 —— 写死字面量就会出现
        // "文案说 150 秒、悬停查到 154 秒前"（2026-11-01 用户报的那次）。
        HistoryTagText.Text = $"最近 {AppState.HistoryWindowSec} 秒";

        // 卡与卡的间隔（含"左卡给右边距会造出宽度差"的老问题）统一在这里发
        UiKit.ApplyCardGap(CardGrid);

        // 行序：温度 → 功耗 → 频率 → …（两卡前三行同序，横着看才对得上）
        _cpuTemp = UiKit.MakeKv(CpuKv, "温度");
        _cpuPower = UiKit.MakeKv(CpuKv, "功耗");
        _cpuFreq = UiKit.MakeKv(CpuKv, "频率");
        _cpuVolt = UiKit.MakeKv(CpuKv, "电压");

        _gpuTemp = UiKit.MakeKv(GpuKv, "温度");
        _gpuPower = UiKit.MakeKv(GpuKv, "功耗");
        _gpuClock = UiKit.MakeKv(GpuKv, "频率");
        _gpuVram = UiKit.MakeKv(GpuKv, "显存");

        Chart.SetLight(UiTheme.Palette == UiPalette.Light);

        // 图例点一下就隐藏/显示那条线（2026-11-03）。六个都接上 —— 少接一个，
        // 用户点到那个没反应，会当成"这条线不让关"。
        // ⚠️ 键名必须与 HistoryChart.SeriesList 里的 Key 逐字一致（那里是唯一真源）。
        Legend(LegCpuT, "cpu_t");
        Legend(LegGpuT, "gpu_t");
        Legend(LegFan, "fan");
        Legend(LegFan2, "fan2");
        Legend(LegCpuP, "cpu_p");
        Legend(LegGpuP, "gpu_p");
    }

    /// <summary>把一块图例接到曲线上：点一下切换显隐，隐藏时那块变淡（但还点得动）。</summary>
    private void Legend(FrameworkElement item, string seriesKey)
    {
        item.MouseLeftButtonUp += (_, _) =>
        {
            var visible = Chart.ToggleSeries(seriesKey);
            item.Opacity = visible ? 1.0 : 0.35;
        };
    }

    /// <summary>
    /// 主题变了（由 <see cref="MainWindow"/> 统一转发）。
    /// ⚠️ 页面**不许**自己订 <see cref="UiTheme.Changed"/>：WPF 关窗时 content 的
    ///    Unloaded 不触发，订阅会漏给下一个窗口的线程 —— 下一次切主题就调到
    ///    死线程上的控件，InvalidOperationException 整条 UI 线程连坐（窗口凭空
    ///    消失 = 用户眼里的"闪退"）。
    /// </summary>
    public void ApplyTheme() => Chart.SetLight(UiTheme.Palette == UiPalette.Light);

    /// <summary>每秒一次。两个参数都可能是 null（没真数据源 / 这一拍读挂了）⇒ 全卡回 "--"，不装。</summary>
    public void Update(StatusDto? status, HistoryDto? history)
    {
        Chart.SetData(history?.Points);
        if (status is null)
        {
            NoData();
            return;
        }

        var cpu = status.Cpu;
        var gpu = status.Gpu;
        var ec = status.Ec;

        // ---------------- 处理器 ----------------
        CpuName.Text = string.IsNullOrEmpty(cpu?.Name) ? "--" : cpu!.Name!;
        CpuPct.Text = Fmt(cpu?.Usage);
        CpuRing.Value = cpu?.Usage ?? 0;

        // 温度走 EC 的读数（与顶栏 CPU 胶囊、散热页同一份）。
        // ⚠️ 取不到就**整行隐藏**（`.Set(null)`），不留「温度 --」—— 未适配机型上
        //    那一行会永远停在 "--"，正是本类开头第 15 行写的"拿不到就不留占位"。
        //    2026-11-02 用 `--device quanta-gmwmi-generic` 预览时才看见它一直空着。
        // ⚠️ 2026-11-02 起未标定机型这一格由 HWiNFO 兜底（后端 AppState.Readings），
        //    数值是真的、来源不是 EC —— 悬停说清楚，别让人以为已经认识这台机器的 EC。
        _cpuTemp.Set(ec?.CpuTemp is long t ? t.ToString(CultureInfo.InvariantCulture) + " °C" : null);
        _cpuTemp.Value.ToolTip = status.Device?.Caps?.EcRead == true
            ? null
            : "本机 EC 读数偏移未标定：这个温度来自 HWiNFO（不是 EC 原生读数）";
        _cpuFreq.Set(cpu?.FreqMhz is double f && f > 0
            ? (f / 1000).ToString("F2", CultureInfo.InvariantCulture) + " GHz"
            : null);

        // 功耗 / 电压：没有数据源就整行隐藏（0 不算"有数据"，0 是"没读到"）
        _cpuPower.Set(cpu?.Power is double p ? p.ToString("F1", CultureInfo.InvariantCulture) + " W" : null);
        _cpuVolt.Set(cpu?.Voltage is double vv && vv > 0
            ? vv.ToString("F3", CultureInfo.InvariantCulture) + " V"
            : null);

        // ---------------- 显卡 ----------------
        if (gpu?.Available == true)
        {
            GpuName.Text = string.IsNullOrEmpty(gpu.Name) ? "--" : gpu.Name!;
            GpuPct.Text = Fmt(gpu.Usage);
            GpuRing.Value = gpu.Usage ?? 0;

            _gpuTemp.Set(gpu.Temp is double gt
                ? gt.ToString("F0", CultureInfo.InvariantCulture) + " °C"
                : null);
            _gpuPower.Set(gpu.Power is double gp
                ? gp.ToString("F1", CultureInfo.InvariantCulture) + " W"
                : null);
            _gpuClock.Set(gpu.Clock is double gc
                ? gc.ToString("F0", CultureInfo.InvariantCulture) + " MHz"
                : null);
            _gpuVram.Set(VramText(status));
        }
        else
        {
            // ⚠️ 独显不可用（切了集显输出 / 重启后读不到 NVIDIA）⇒ **全部复位**。
            //    不写 else 的版本会让显卡卡一直停在重启前的旧读数，而顶栏已经显示 "--"。
            //    复位 = 整行藏起来（不留 "--"），卡片本身留着（名字那里说明是谁）。
            GpuName.Text = "不可用";
            GpuPct.Text = "--";
            GpuRing.Value = 0;
            _gpuTemp.Set(null);
            _gpuPower.Set(null);
            _gpuClock.Set(null);
            _gpuVram.Set(null);
        }
    }

    /// <summary>
    /// 显存：`已用 / 容量 (百分比)`，结温只有 HWiNFO 给，有就并进同一行省高度。
    /// 取不到总容量 ⇒ **null**（整行隐藏），不是 "--"（与本类头的约定一致）。
    /// </summary>
    private static string? VramText(StatusDto status)
    {
        var gpu = status.Gpu;
        if (gpu?.MemTotal is not double total || total <= 0) return null;

        var used = gpu.MemUsed ?? 0;
        var percent = used / total * 100;
        var text = (used / 1024).ToString("F1", CultureInfo.InvariantCulture)
                   + " / " + (total / 1024).ToString("F0", CultureInfo.InvariantCulture)
                   + " GB (" + percent.ToString("F0", CultureInfo.InvariantCulture) + "%)";

        if (status.Hwmon?.Gpu?.MemTemp is double mem && mem > 0)
            text += " · " + mem.ToString("F0", CultureInfo.InvariantCulture) + "°C";

        return text;
    }

    private void NoData()
    {
        CpuName.Text = "--";
        GpuName.Text = "--";
        CpuPct.Text = "--";
        GpuPct.Text = "--";
        CpuRing.Value = 0;
        GpuRing.Value = 0;
        _cpuTemp.SetAlways("--");
        _cpuPower.SetAlways("--");
        _cpuFreq.SetAlways("--");
        _cpuVolt.SetAlways("--");
        _gpuTemp.SetAlways("--");
        _gpuPower.SetAlways("--");
        _gpuClock.SetAlways("--");
        _gpuVram.SetAlways("--");
    }

    /// <summary>整数（对应 JS 的 fmt(n) = toFixed(0)）。null ⇒ "--"。</summary>
    private static string Fmt(double? value)
        => value is double v ? v.ToString("F0", CultureInfo.InvariantCulture) : "--";

    // ================================================================ 历史导出

    /// <summary>
    /// 「导出 CSV」——把**保留期内的全部采样**（history\hist-*.csv，最多 7 天）导成一份 CSV。
    ///
    /// 为什么先弹保存对话框再调后端（而不是像机型报告那样固定落 logs\）：
    /// 这份数据是给用户拿去 Excel/脚本里分析的，落点该由他决定；而且后端只认路径，
    /// 保存对话框的事归界面 —— 后端跑在托盘里时没有窗口可弹。
    ///
    /// ⚠️ 导出要读 7 天的文件（十几万行），必须在后台线程做，否则界面会僵住几百毫秒。
    /// ⚠️ 一次只跑一个：连点两下会同时写两个文件、弹两条 toast。
    /// </summary>
    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting) return;

        if (!_api.HasState)
        {
            Toast("后端没有初始化（没找到 model.json），导不出历史", false);
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出历史数据",
            FileName = "qcontrol-history-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".csv",
            DefaultExt = ".csv",
            Filter = "CSV 文件 (*.csv)|*.csv",
            AddExtension = true,
            OverwritePrompt = true,
        };

        // 传窗口做 owner：否则对话框可能跑到主窗口后面去（用户以为点了没反应）
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        _exporting = true;
        ExportCsvButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() =>
                _api.Post("/api/history/export", new JsonObject { ["path"] = dlg.FileName }));

            if (!result.Ok)
            {
                Toast(result.Why("导出失败"), false);
                return;
            }

            var rows = result.Raw?["rows"]?.GetValue<int>() ?? 0;
            Toast($"已导出 {rows} 行 → {System.IO.Path.GetFileName(dlg.FileName)}", true);
            Console.WriteLine($"[history] 界面导出 {rows} 行 → {dlg.FileName}");
        }
        catch (Exception ex)
        {
            Toast("导出失败：" + ex.Message, false);
        }
        finally
        {
            _exporting = false;
            ExportCsvButton.IsEnabled = true;
        }
    }

    private void Toast(string text, bool ok) => (Window.GetWindow(this) as MainWindow)?.Toast(text, ok);
}
