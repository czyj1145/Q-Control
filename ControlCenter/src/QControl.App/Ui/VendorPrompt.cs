using System.Windows;

namespace QControl.App.Ui;

/// <summary>用户在「采集前先打开原厂」那道门槛上的选择。</summary>
internal enum VendorGate
{
    /// <summary>照引导采（原厂界面已就绪，或者程序状态读不到、不拦）。</summary>
    Guide,

    /// <summary>用户明确选了跳过引导：观察窗口传 0，只采本机这一半。</summary>
    SkipGuide,

    /// <summary>这次不采了。</summary>
    Cancel,
}

/// <summary>
/// 「一键采集 / 只读采集」开始前的**唯一一道确认框** —— 原厂门槛和"会写 EC"合成一屏。
///
/// 为什么必须弹（2026-11-22 用户要求）：采集的第 2 段（引导原厂）全靠抓原厂真实下发的
/// 写命令来认性能档 / 键盘灯 / 风扇的语义。原厂没跑、或者进程在但界面关到托盘里，这一段
/// 就白等 150 秒 —— 用户以为自己"照着做了"，交上来的包却永远差那一半能力，还得重采。
/// 以前这句提醒只写在确认框正文末尾（"要求原厂正在运行"），等于没提醒。
///
/// 三种原厂状态三种说法，不合并成一句"请打开原厂控制中心"：
///   · <see cref="VendorUiState.NotRunning"/> —— 连进程都没有，得先启动它；
///   · <see cref="VendorUiState.NoWindow"/>   —— 进程在（daemon），窗口关着，要去托盘点开；
///   · <see cref="VendorUiState.WindowReady"/> —— 界面开着，只提醒"等下照小窗做、别关它"。
/// 界面开着时**照样弹**：用户要的是"点下去之前知道接下来会发生什么"，而且这框同时还承担
/// 写 EC 的确认 —— 分成两屏连着弹两次才是真烦。
///
/// 选「我已打开」之后**重新探测**（原厂启动要一两秒才出窗口），还不对就再问一次并说明
/// 没探到什么 —— 不做"等两秒就当它开了"的假设，那等于让用户对着关着的界面点满 150 秒。
/// </summary>
internal static class VendorPrompt
{
    /// <param name="writes">这一轮会不会写 EC（完整采集 = true，只读采集 = false）。</param>
    /// <param name="probe">取一次原厂界面状态。每次判定/复核都现问，不用缓存。</param>
    public static async Task<VendorGate> AskAsync(FrameworkElement ctx, bool writes, Func<VendorUiState> probe)
    {
        var retry = "";
        while (true)
        {
            var state = probe();
            var picked = await ShowAsync(ctx, state, writes, retry);

            if (picked is null or "cancel") return VendorGate.Cancel;
            if (picked == "skip") return VendorGate.SkipGuide;

            // 界面本来就开着（或读不到状态）：点「开始采集」就是真的开始
            if (state is VendorUiState.WindowReady or VendorUiState.Unknown) return VendorGate.Guide;

            // 原厂刚启动时主窗口要一两秒才出来：给一拍再探，别把"正在起"判成"没开"
            await Task.Delay(1200);
            var after = probe();
            if (after is VendorUiState.WindowReady or VendorUiState.Unknown) return VendorGate.Guide;

            retry = after == VendorUiState.NoWindow
                ? "还是没找到原厂界面。\n\n"
                : "还是没检测到原厂在运行。\n\n";
        }
    }

    /// <summary>
    /// 把对话框摆出来，返回选中的 key（`start` / `skip` / `cancel`）。
    /// 单拎出来是为了 `--ui-dialog vendor-*` 能拍**同一份文案** —— 截图专用的副本迟早走散。
    /// </summary>
    internal static Task<string?> ShowAsync(FrameworkElement ctx, VendorUiState state, bool writes,
                                            string retry = "")
    {
        var ready = state is VendorUiState.WindowReady or VendorUiState.Unknown;

        var title = state switch
        {
            VendorUiState.NotRunning => "先打开原厂控制中心",
            VendorUiState.NoWindow => "原厂在后台，请打开它的界面",
            _ => writes ? "开始采集？" : "开始只读采集？",
        };
        var okText = state switch
        {
            VendorUiState.NotRunning => "我已打开",
            VendorUiState.NoWindow => "已打开界面",
            _ => writes ? "开始采集" : "开始只读采集",
        };

        var body = VendorPart(state);
        // 原厂还没就绪时说清"跳过"的代价：那颗按钮就在下面，别让它显得没成本
        if (!ready) body += "\n\n跳过引导也能采完，但性能档 / 背光 / 风扇只能靠猜。";
        body += "\n\n" + (writes ? WritePart : ReadOnlyPart);

        return UiDialog.ChooseAsync(ctx, title, retry + body,
        [
            ("start", okText, true, false),
            ("skip", "跳过引导", false, false),
            ("cancel", "取消采集", false, false),
        ]);
    }

    /// <summary>原厂那一段（三态各一句）。</summary>
    private static string VendorPart(VendorUiState state) => state switch
    {
        VendorUiState.NotRunning =>
            "引导要靠原厂下发的写命令来认性能档 / 键盘灯 / 风扇 —— 它没在运行，这一段就抓不到东西。\n\n"
            + "先打开原厂控制中心（开始菜单里的 ControlCenter），不用先切什么，再点「我已打开」。",

        VendorUiState.NoWindow =>
            "原厂在后台跑着（Daemon），但界面关着 —— 引导要在界面里切档，没有界面就没地方点。\n\n"
            + "从右下角托盘或开始菜单打开它的界面（最小化也算没打开），再回来继续。",

        _ => "采集时会在原厂上面浮出置顶小窗，照步骤切性能三档 / 键盘灯 / 风扇，每步点「完成这步」。",
    };

    /// <summary>完整采集那一段：写什么、写几次、会响会热，一次说清（"可能修改设置"等于没说）。</summary>
    private const string WritePart =
        "会写两次 EC：性能档、背光，各读回后恢复（约 15 秒，不碰风扇）；最后 CPU 满载 30 秒（会热会响）。"
        + "只想读就取消，改点「只读采集」。";

    /// <summary>只读采集那一段：不写、但照样花时间。</summary>
    private const string ReadOnlyPart =
        "全程不写 EC：只读数据、只抓原厂下发的写命令；最后仍有 CPU 满载 30 秒（会热会响）。";
}
