namespace QControl.Core.Ec;

/// <summary>第三路（SYS）风扇的运行时判定结论。</summary>
public enum ThirdFanVerdict
{
    /// <summary>还没结论：继续按节奏采样（也可能这台就是两风扇，永远停在这里）。</summary>
    Waiting,

    /// <summary>连续读到可信的 SYS 转速 ⇒ 这台有三风扇。</summary>
    HasThirdFan,
}

/// <summary>
/// 「这台机器到底有没有第三路风扇」的**运行时**判定 —— 纯逻辑，不碰 EC，可用假时钟单测。
///
/// <para>
/// 为什么不能只靠 SKU / 机型名：
/// <list type="bullet">
/// <item>原厂判据 `IsSupport3Fan = (sku-52)<=1` 是**型号档位标志**，不是物理风扇数
///   （SKU 52 那台实机 `0x0207` 的 a3 = 0，明确没第三路）；</item>
/// <item>第三方 <c>FanControlPortable</c> 判 SKU 51/52，同样是表，不是实测；</item>
/// <item>同模具换个品牌/贴牌，SMBIOS 名字认不出来时档案会退回家族默认（两风扇），
///   那台真三风扇机的 SYS 行就永远不出现 —— 而这台机器我们手上没有，验不了。</item>
/// </list>
/// 所以再加一道**只看固件自己报的数**的判据：`0x0207` 的 a3（SYS 转速）连续两次落在
/// 可信区间，就认定有第三路。两风扇机上这个字段实测恒为 0（本机 SKU 0x38 反复读都是 0），
/// 于是这条判据在两风扇机上**恒不成立**，不会凭空多画一个圈。
/// </para>
///
/// <para>
/// ⚠️ 只认「非 0 且合理」的转速，**不认温度**：a2 在两风扇机上是 GPU 温度的镜像
/// （本机实测同值），拿它当"有 SYS 传感器"的证据会误判；而三风扇机的 SYS 风扇
/// 在低温时也可能停转（0 RPM），那时就先不升级，等它转起来。
/// </para>
///
/// <para>
/// 节奏：前 20 次每 30 s 采一次（覆盖开机/冷启动把风扇从停转带到起转的那几分钟），
/// 之后每 5 min 一次 —— 两风扇机上等于每分钟多一次只读的 `0xFA00` 往返，
/// 可以忽略；三风扇机上判出来就永久停止采样。
/// </para>
///
/// <para>
/// **负向证据（2026-11-22 加）**：两风扇机上 `0x0207` 的 a2 是 **GPU 温度的逐拍镜像**
/// （本机实测 6 拍 52/53 完全同步、a3=0）。连续 <see cref="QuietAfterMirrors"/> 拍
/// 「a3=0 且 a2 贴着 GPU 温度」⇒ 基本可以认定这条命令在本机没有 SYS 语义，
/// 采样间隔放慢到 <see cref="QuietIntervalSeconds"/>（30 min），少一点无谓的 EC 往返。
/// </para>
/// <para>
/// ⚠️ 但它**不是判决**，只是降速：一旦 a2 偏离 GPU 温度（三风扇机上 SYS 有独立传感器，
/// 迟早会偏），或哪一拍读到可信 a3，立刻回到密集采样/升级 —— 宁可慢一拍，也不给三风扇机
/// 判死刑（"这台就是没有第三路"这种事，我们从固件读数里证不了）。
/// </para>
/// </summary>
public sealed class ThirdFanProbe
{
    /// <summary>可信转速下限：低于这个数当"风扇停转 / 没这路"，不当证据。</summary>
    public const int MinRpm = 300;

    /// <summary>可信转速上限：本机两路风扇实测 2300~5100，8000 足够覆盖满转。</summary>
    public const int MaxRpm = 8000;

    /// <summary>SYS 温度上限（°C）：a3 有值但 a2 是野数时也别认。</summary>
    public const int MaxTempC = 125;

    /// <summary>要连续几次可信读数才升级 —— 一次可能是串帧/脏值。</summary>
    public const int ConfirmHits = 2;

    /// <summary>「a2 等于 GPU 温度」的容差（°C）：两个传感器同值不是逐位相等，差 1 度很正常。</summary>
    public const int MirrorToleranceC = 1;

    /// <summary>连续几次「a3=0 且 a2 镜像 GPU 温度」才认定这条命令在本机没有 SYS 语义。</summary>
    public const int QuietAfterMirrors = 4;

    /// <summary>降速后的采样间隔（秒）：30 min 一次，只读命令，几乎零成本。</summary>
    public const int QuietIntervalSeconds = 1800;

    private double _lastAt = double.NegativeInfinity;
    private int _mirrorHits;

    /// <summary>已经采过几次（诊断用）。</summary>
    public int Samples { get; private set; }

    /// <summary>连续可信次数（中间出现不可信就归零）。</summary>
    public int Hits { get; private set; }

    /// <summary>连续"a2 镜像 GPU 温度"的次数（负向证据，见类注释）。</summary>
    public int MirrorHits => _mirrorHits;

    /// <summary>
    /// 已进入降速模式（这条命令本机听起来没有 SYS 语义）。**不是判决**：
    /// a2 一旦偏离 GPU 温度或读到可信 a3 就自动退出。
    /// </summary>
    public bool Quiet { get; private set; }

    public ThirdFanVerdict Verdict { get; private set; } = ThirdFanVerdict.Waiting;

    /// <summary>还要不要继续采。判出三风扇之后就没必要了。</summary>
    public bool Pending => Verdict == ThirdFanVerdict.Waiting;

    /// <summary>采样间隔（秒）：降速 30 min；否则前 20 次 30 s，之后 5 min。</summary>
    public double IntervalSeconds
        => Quiet ? QuietIntervalSeconds : Samples < 20 ? 30 : 300;

    /// <summary>这一拍该不该去读 `0x0207`。</summary>
    public bool ShouldSample(double now) => Pending && now - _lastAt >= IntervalSeconds;

    /// <summary>
    /// 喂一次读数。<paramref name="tempC"/> / <paramref name="rpm"/> 就是
    /// `0x0207` 的 a2 / a3（已按档案 `sys_layout` 的长度取好）；
    /// <paramref name="gpuTempC"/> 是同一拍的 GPU 温度，**只有传了才做镜像统计**
    /// （布局未标定的机型连 GPU 温度都没有，那就只按 30 s / 5 min 慢慢探）。
    /// </summary>
    public ThirdFanVerdict Observe(double now, long tempC, long rpm, long? gpuTempC = null)
    {
        _lastAt = now;
        Samples++;

        if (Verdict != ThirdFanVerdict.Waiting) return Verdict;

        if (LooksLikeThirdFan(tempC, rpm))
        {
            Hits++;
            _mirrorHits = 0;
            Quiet = false;                  // 「有转速」比任何负向证据都强
            if (Hits >= ConfirmHits) Verdict = ThirdFanVerdict.HasThirdFan;
            return Verdict;
        }

        // 不可信就打断连击：串帧/脏值混进来不能被下一次"顺带"凑满。
        Hits = 0;

        // 负向证据：a3=0 且 a2 贴着 GPU 温度 ⇒ 这台大概是"这条命令没 SYS 语义"。
        // 只降速、不判决（见类注释）。
        if (rpm == 0 && gpuTempC is { } g && Math.Abs(tempC - g) <= MirrorToleranceC)
        {
            if (++_mirrorHits >= QuietAfterMirrors) Quiet = true;
        }
        else
        {
            // 温度偏离 GPU ⇒ 可能有独立 SYS 传感器，回到密集采样（哪怕已经降速过）
            _mirrorHits = 0;
            Quiet = false;
        }
        return Verdict;
    }

    /// <summary>单次读数像不像"真有一路 SYS 风扇在转"。</summary>
    public static bool LooksLikeThirdFan(long tempC, long rpm)
        => rpm is >= MinRpm and <= MaxRpm && tempC is >= 0 and <= MaxTempC;
}
