namespace QControl.Core.FanCurve;

/// <summary>
/// 风扇曲线引擎要碰硬件的部分。**接口放在 Core 里**，实现放在 App（借 Hal 的 EcHal）。
///
/// 这样 Core 仍然是 `net10.0`（零 Windows 依赖），而状态机本身可以在无真机、
/// 无管理员的情况下用假硬件跑单测 —— 这正是「曲线把风扇钉在 30%」那类事故
/// 最该被测住的地方。
/// </summary>
public interface IFanHardware
{
    /// <summary>配置文件里的 EC 写入开关（`presets.ec.enabled`）。</summary>
    bool WritesEnabled { get; }

    /// <summary>机型层面的最终写许可（`Device.WritesAllowed`）。</summary>
    bool WritesAllowed { get; }

    /// <summary>当前进程是不是管理员 —— 非管理员时 EC 写"被接受但不生效"。</summary>
    bool AdminOk { get; }

    /// <summary>非管理员时的提示语（`env_info().hint`），拼进错误消息里。</summary>
    string? AdminHint { get; }

    /// <summary>EC 里当前钉着的 duty（我们或别人写进去的）。没写过就是 null。</summary>
    int? DutyApplied { get; }

    /// <summary>
    /// 当前是否处于「未开自定义风扇、用户点了最大转速」的**临时满转**态
    /// （<c>EcHal.OemFanMax</c>，见 <c>EcHal.SetOemFanMax</c>）。
    ///
    /// <para>
    /// 引擎需要知道它，是因为这时 EC 里钉的 100 **不是曲线写的**、也不归属引擎：
    /// FN+1 进来/退出去都不该再去写一脚 UFA —— 写了会经 <c>EcHal.SetFans</c>
    /// 把临时满转标志清掉，用户那颗按钮就从「恢复自动」退回「最大转速」，
    /// 而风扇仍在 100%，等于把这个状态弄丢了（2026-11-25 审查发现）。
    /// </para>
    /// <para>默认 false：这是控制中心自己的概念，其它实现（含测试桩）不受影响。</para>
    /// </summary>
    bool OemFanMax => false;

    /// <summary>
    /// 写三个风扇槽（UFA1/UFA2/UFA3 = CPU 风扇 / GPU 风扇 / SYS 风扇）。
    /// <para>
    /// **按原厂语义分路**（2026-10-23 实证，见 <c>docs/PROTOCOL.md</c> §7.3）：
    /// UFA1 ← CPU 曲线@CPU 温度，UFA2 ← GPU 曲线@GPU 温度，UFA3 ← SYS 曲线。
    /// 传 null 的槽保持 EC 里的当前值（<c>EcHal</c> 的既有语义）。
    /// </para>
    /// 被拒绝时抛 <see cref="FanWriteDeniedException"/>。
    /// </summary>
    void SetFans(int? cpu, int? gpu, int? sys = null);

    /// <summary>
    /// **把风扇交回 EC 固件**（清接管标志 + 三路 UFA 写 0xFF，见 <c>EcHal.ReleaseToEc</c>）。
    ///
    /// <para>
    /// 默认实现是 no-op：只有声明了 `ec.cmd.omcc` + `ec.cmd.fan` 的机型才真能交回。
    /// 不认识这条命令的机型维持既有语义 —— 我们收手不写，EC 保持最后收到的转速。
    /// 失败抛 <see cref="FanWriteDeniedException"/>（确定性失败，调用方如实报）。
    /// </para>
    /// </summary>
    void ReleaseToEc() { }

    /// <summary>读温度。读数超过 <paramref name="maxAge"/> 视为没有（返回 null）。</summary>
    (double? Cpu, double? Gpu) Temps(TimeSpan maxAge);

    /// <summary>
    /// 第三路（SYS / 系统风扇）的温度。**默认 null = 这台机器没有第三路** ——
    /// 两风扇机型的实现一个字都不用改。
    ///
    /// <para>
    /// SYS 温度不在 <c>0x0200</c> 那帧里，是另一条子命令（`0x0207` 的 a2，
    /// 原厂 `ControlCenterDaemon.GetHardwareInformation2` 实证，见 docs/PROTOCOL.md §七·补）。
    /// 所以它是**独立的一路读数**，不能塞进 <see cref="Temps"/> 的返回值里 ——
    /// 两风扇机型压根不该为了它多发一条命令。
    /// </para>
    /// </summary>
    double? SysTemp(TimeSpan maxAge) => null;
}

/// <summary>
/// EC 写入被拒。这是**确定性失败**（开关被关、权限没了），必须自停并报原因，
/// 不能像普通抖动那样下一轮重试。
/// </summary>
public sealed class FanWriteDeniedException(string message) : Exception(message);
