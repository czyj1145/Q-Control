namespace QControl.App.Http;

/// <summary>
/// 路由表 = **契约的字面清单**：路径、方法与 API 契约一一对应，不多不少。
/// 改这里等于改契约，`tests/fixtures` 里的样本要同步。
///
/// 分发顺序：GET 优先真数据、取不到才落回桩样本；POST 交给
/// <see cref="PostHandlers"/>，没接上的才回 501 —— 详见 ApiResponse.Unimplemented。
/// </summary>
internal static class Router
{
    /// <summary>GET 端点 → 桩数据文件名（对应 tests/fixtures/&lt;name&gt;.json）。</summary>
    private static readonly Dictionary<string, string> GetStubs = new(StringComparer.Ordinal)
    {
        ["/api/status"] = "status",
        ["/api/hardware"] = "hardware",
        ["/api/history"] = "history",
        ["/api/power/plans"] = "power_plans",
        ["/api/fancurve"] = "fancurve",
        ["/api/gpu/info"] = "gpu_info",
        ["/api/gpu/oc"] = "gpu_oc",
        ["/api/app/settings"] = "app_settings",
        // 「关于」页（2026-10-20）：版本/机型/端口/启动时间 + 日志现状与末尾 8 行。
        // 桩只服务于纯桩模式（没找到 model.json），真机一律走 AppState.About()。
        ["/api/app/about"] = "app_about",
        ["/api/app/log/usage"] = "app_log_usage",
        // 检查更新（2026-10-27）：内存状态，一次请求都不发往更新源。
        // 真正的查询由 POST /api/app/update/check 在后台跑，界面轮询这个接口取结果。
        ["/api/app/update"] = "app_update",
        ["/api/vendor/state"] = "vendor_state",
        ["/api/osd"] = "osd",
        ["/api/device"] = "device",
        ["/api/model"] = "model",
        // 诊断口：真实实现总会回数据（它会当场 ensure 一次 HWiNFO）；
        // 桩只用于纯桩模式下别落成 404。
        ["/api/debug/hwinfo"] = "debug_hwinfo",
        // 诊断口：进程内存拆账（工作集 / 提交 / 托管堆各代 / 最近一次归还工作集）。
        // 2026-10-27 加，回答「还能不能再压」时用它，别再靠猜。
        ["/api/debug/mem"] = "debug_mem",
    };

    /// <summary>POST 端点全集。**路径必须存在**，否则界面会当 404 处理。</summary>
    private static readonly Dictionary<string, string> PostEndpoints = new(StringComparer.Ordinal)
    {
        // ---- 电源 / 风扇 ----
        ["/api/power/plan"] = "切换 Windows 电源计划（guid）",
        ["/api/display/brightness"] = "内建屏亮度（0~100，WMI；无需管理员）",
        ["/api/fan/mode"] = "手动风扇开 / 关（manual ↔ auto）",
        // ---- EC 直写 ----
        ["/api/ec/selftest"] = "写一次背光并读回，验证写入是否生效",
        ["/api/ec/fan"] = "手动风扇转速 cpu/gpu/sys（duty 钳 [30,100]；需先开启软件曲线）",
        ["/api/ec/perf"] = "性能模式 APMD（高危，需先 arm）",
        ["/api/ec/backlight"] = "键盘背光；带 zones 时走三分区染色",
        ["/api/backlight/custom-override"] = "自定义接管（开=始终用第四套 custom 灯效）",
        ["/api/ec/gfx"] = "显卡模式 GFXT（双面命令，必须走写组；重启生效）",
        ["/api/ec/arm"] = "高危命令的二次确认授权",
        ["/api/ec/raw"] = "逆向调试：下发任意参数命令",
        // ---- GPU 超频（NVML）----
        ["/api/gpu/oc"] = "应用显存 / 核心超频",
        ["/api/gpu/oc/reset"] = "超频复位",
        // ---- 应用设置 ----
        ["/api/app/settings"] = "开机自启 / 启动方式 / 主题 / OSD",
        // 键盘：低级键盘钩子吞掉左右 Win 键（进程内；控制中心退出即自动恢复）
        ["/api/keyboard/win-lock"] = "锁定 Win 键（打游戏防误触；不写注册表，退出即恢复）",
        // 日志区间：开始打标记 → 结束时把这一段单独导出成 logs/capture-*.log
        ["/api/app/log/capture/start"] = "开始区间记录（在日志里打开始标记）",
        ["/api/app/log/capture/stop"] = "结束区间记录并把这段导出成独立文件",
        ["/api/app/log/open"] = "在资源管理器里打开日志目录",
        ["/api/app/log/clear"] = "清理日志文件（当天/轮转旧档/区间导出；body {packs:true} 连采集包、{profiles:true} 连机型档案一起清）",
        // 机型适配：把本机身份/EC 原始帧导出成一份报告，交给作者补 devices/*.json。
        // 只读采集（不写 EC、不改配置），save=false 时只在响应里带回全文（复制用）。
        // adopt=true = 一键机型档案采集：额外采 20 秒帧、自动判偏移、把档案写进本机 devices\。
        // adopt+probe=true = 连写入验证一起做（性能档 APMD + 键盘背光，写后读回、写完恢复）。
        ["/api/app/machine-report"] = "导出机型适配报告 / 一键采集本机档案（logs/adopt-* + devices/*.json）",
        // 历史采样：落盘在 history\hist-*.csv（7 天滚动），这里把保留期内的数据导出一份 CSV。
        // 只读采样文件 + 写用户指定的一个文件，不碰 EC、不改配置。
        ["/api/history/export"] = "导出历史采样 CSV（默认保留期全部，path 可选）",
        // 只查版本：GitHub → Gitee 回退查一次最新发布（下载/安装是下面三条）
        ["/api/app/update/check"] = "检查新版本（后台查，立刻返回；界面轮询 /api/app/update）",
        // 下载与安装：包落在 %LocalAppData%\Q-Control\update\<tag>\，校验后才提权起安装器
        ["/api/app/update/download"] = "下载最新版的安装包（后台；界面轮询 download 段看进度）",
        ["/api/app/update/cancel"] = "取消下载并删掉半截文件",
        ["/api/app/update/install"] = "安装已下载的包（SHA256 校验通过后才启动安装器，需管理员）",
        ["/api/app/quit"] = "优雅退出（唯一非界面调用者：stop.cmd）",
        ["/api/hardware/refresh"] = "后台重采硬件档案（只读）",
        ["/api/osd"] = "弹一条 OSD 提示",
        ["/api/vendor/stop"] = "退出原厂控制中心进程",
        // ---- HWiNFO 诊断 / 修复 ----
        ["/api/hwinfo/restart"] = "杀掉所有实例再拉起",
        ["/api/hwinfo/foreign-sm"] = "给用户自己的实例打开共享内存导出",
        ["/api/debug/hwinfo/restart"] = "诊断：重启 HWiNFO 看共享内存是否恢复",
        // 诊断：当场 GC + 归还工作集，回前后数字（平时由关窗 / 空闲自动触发）
        ["/api/debug/mem/trim"] = "诊断：当场回收并归还工作集（回前后 MB）",
        // ---- 风扇曲线 ----
        ["/api/fancurve/start"] = "启动曲线接管（自定义风扇未开启时拒绝）",
        // 软件曲线总开关：false = 交回 EC 固件（出厂默认），true = 软件曲线接管
        ["/api/fancurve/enabled"] = "自定义风扇总开关 enabled:true/false（默认 false = 交回 EC 固件）",
        ["/api/fancurve/config"] = "曲线（CPU/GPU 双路）/ 温度墙 / 绑定模式（唯一写入口；apply_now 立刻生效）",
        ["/api/fancurve/mode-curve"] = "取某档位的曲线给界面编辑（只读）",
        ["/api/fancurve/ack-switch"] = "「切模式换了曲线」提示已播报",
        // 原厂三档曲线只读导入：预览不写任何配置；导入只改我们的 perf.modes 档案
        ["/api/fancurve/oem-preview"] = "只读查看原厂 UserFan1/2/3 三档曲线（预览，不导入）",
        ["/api/fancurve/import-oem"] = "把原厂三档 UserFan 曲线导入到 perf.modes 档案",
        // ---- 配置读写 ----
        ["/api/model/preset"] = "按 path 写配置项（一级 path = 按节合并多个键）",
        ["/api/model/reset"] = "恢复出厂（可选单个 section）",
        // ---- 系统 ----
        ["/api/system/reboot"] = "重启（shutdown.exe，不加 /f；seconds=0 立刻、不弹窗）",
        ["/api/system/reboot/cancel"] = "取消计划重启",
    };

    /// <summary>全部被冻结的端点（自检 / 文档用）。</summary>
    public static IEnumerable<EndpointSpec> AllEndpoints
    {
        get
        {
            foreach (var p in GetStubs.Keys)
                yield return new EndpointSpec("GET", p, "桩：回 fixtures 真机样本");
            foreach (var (p, desc) in PostEndpoints)
                yield return new EndpointSpec("POST", p, desc);
        }
    }

    public static int Count => GetStubs.Count + PostEndpoints.Count;

    /// <summary>
    /// 能直接回真数据的端点。返回 null 表示"这次取不到"，落回桩数据。
    ///
    /// 为什么不用一个 `IService` 接口：这里的差别只有"取哪份 JSON"，
    /// 中间多一层抽象不换来任何好处，反而让"哪个端点还没接"更难看清。
    /// </summary>
    private static Func<string, byte[]?>? _realSource;

    /// <summary>挂上真数据源（App 启动时调用）。不挂就是纯桩模式。</summary>
    public static void UseRealData(Func<string, byte[]?>? source) => _realSource = source;

    /// <summary>
    /// 已实现的写操作。返回 null 表示"这条还没接"，由 <see cref="RoutePost"/> 落回 501。
    ///
    /// 单独一个钩子而不是把逻辑写进 Router：写操作要碰 `AppState`（EC / 模型 / 进程），
    /// 那属于 App 的职责；Router 只负责"这条路径存不存在"。
    /// </summary>
    private static Func<ApiRequest, ApiResponse?>? _postSource;

    public static void UseRealPost(Func<ApiRequest, ApiResponse?>? handler) => _postSource = handler;

    /// <summary>分发。只认 /api/；其余路径一律 404（界面只有 WPF，不再有静态资源）。</summary>
    public static ApiResponse Route(ApiRequest req, StubData stubs)
    {
        if (!req.Path.StartsWith("/api/", StringComparison.Ordinal))
            return ApiResponse.NotFoundHtml();

        return req.Method switch
        {
            // HEAD 与 GET 同语义，只是不写 body（MiniHttpServer 据此跳过 body）。
            // 不单独回 404 —— 排错时 `curl -I` 是最常用的手段，回 404 会让人以为路由没注册。
            "GET" or "HEAD" => RouteGet(req, stubs),
            "POST" => RoutePost(req),
            _ => ApiResponse.NotFoundHtml(),
        };
    }

    private static ApiResponse RouteGet(ApiRequest req, StubData stubs)
    {
        if (!GetStubs.TryGetValue(req.Path, out var stubName))
            return ApiResponse.NotFoundHtml();      // GET 未命中 = HTML 404（见 NotFoundJson 的注释）

        // ① 优先真数据：能取到的就用真的，取不到才落回桩。
        if (_realSource is not null)
        {
            var live = _realSource(req.Path);
            if (live is { Length: > 0 })
                return new ApiResponse(200, ApiResponse.JsonContentType, live);
        }

        // ② 桩数据缺失 = 明确的配置错误，绝不能回 {} 让界面以为"字段全空"。
        if (!stubs.TryGet(stubName, out var body))
            return ApiResponse.Json(
                $$"""{"ok":false,"error":"桩数据缺失：tests/fixtures/{{stubName}}.json 不存在或读不出"}""",
                500);

        return new ApiResponse(200, ApiResponse.JsonContentType, body, IsStub: true);
    }

    private static ApiResponse RoutePost(ApiRequest req)
    {
        // ① 已实现的写操作。返回 null = "这条还没接"，落到下面。
        if (_postSource?.Invoke(req) is { } real) return real;

        // ② 路径存在但没实现 = 501（明确说"尚未实现"，绝不回 ok:true）
        return PostEndpoints.ContainsKey(req.Path)
            ? ApiResponse.Unimplemented("POST", req.Path)
            : ApiResponse.NotFoundJson();          // POST 未命中 = JSON 404（与 GET 不同，照搬早先实现）
    }
}
