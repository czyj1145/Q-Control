using System.Diagnostics;
using System.Text.Json.Nodes;
using QControl.Core.Devices;

namespace QControl.App;

/// <summary>
/// 「加一段 CPU 负载，看哪路风扇起来」—— 一键采集里的**因果**证据段。
///
/// 为什么要自己造负载：温度偏移靠"跟谁一起动"能算，风扇归属却算不出来 ——
/// 一台闲置的机器上两路转速都是常数，相关性没有意义；而用户自己也未必愿意开游戏。
/// 这里在采集快结束时给 CPU 加 30 秒满载（几个普通线程，低于普通优先级），
/// 期间按 250ms 采帧，于是"哪一路转速跟着起来"变成了实打实的观测。
/// 30 秒是 2026-11-14 真机两版对比后定下来的：13 秒只把温度推了 2°C，够不到风扇拐点
/// （63→65°C，而多数模具 70°C 以上才明显加速）；拉长到 30 秒才可能把温升做过拐点。
///
/// 边界（报里都写明，不装懂）：
///   · **只加 CPU，不加 GPU**：程序里没有可用的 GPU 计算/渲染通道（界面是软件渲染），
///     所以结论只到"哪一路是 CPU 风扇"，另一路按"剩下的那路"记作 GPU 风扇；
///   · 很多模具的固件是**按整机最高温同时驱动两路**，那种机器两路一起升，
///     这个方法分不出来 —— 由 <see cref="FanRoleAdvisor"/> 判成"不判"，不硬给结论；
///   · 离电 / 限频的机器上负载可能拉不起功耗，这一轮同样只留下"没拉起来"的记录。
///
/// 安全：只跑普通用户态线程，不加电压、不改频率、不写 EC；约 40 秒后一定停（finally 里停）。
/// 期间暂停自己的风扇曲线 —— 它每秒写一次 UFA，会和"固件自己在调速"混在一起。
/// </summary>
internal static class CpuLoadProbe
{
    /// <summary>总时长：3 秒基线 + 30 秒负载 + 5 秒恢复。</summary>
    public const int TotalSeconds = 38;

    private const int BaseMs = 3000;
    private const int LoadMs = 30000;
    private const int SampleMs = 250;

    /// <summary>这一段的产出：给推断用的三段帧 + 给采集包的摘要。</summary>
    public sealed record Result(
        JsonObject Json,
        IReadOnlyList<LayoutSample> Baseline,
        IReadOnlyList<LayoutSample> Loaded,
        IReadOnlyList<LayoutSample> Recovery,
        bool Ran,
        string? Skip);

    public static Result Run(AppState st, Action<string> stage)
    {
        stage ??= _ => { };

        var (ecOk, ecWhy) = st.Ec.Transport.Available();
        if (!ecOk) return Skipped("连不上 EC（" + (ecWhy ?? "原因未知") + "）");
        if (EcSampling.StatusFrame(st) is null) return Skipped("读不到 status 帧（档案里没有 status 命令）");

        var baseline = new List<LayoutSample>();
        var loaded = new List<LayoutSample>();
        var recovery = new List<LayoutSample>();

        var fanWasRunning = st.Fan.Running;
        // 上一段（引导观察）里点过「结束」的话这个标记还是 true —— 不清掉这一段会一帧都采不到。
        st.ResetObserveStop();
        var sw = Stopwatch.StartNew();
        var stopBurnAt = BaseMs + LoadMs;
        var durationMs = TotalSeconds * 1000;
        var threads = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);

        var burnStarted = false;
        var burnStopped = false;
        long lastSample = -SampleMs;

        stage("空载记风扇基线…");

        try
        {
            if (fanWasRunning) st.Fan.Stop(release: false);

            while (sw.ElapsedMilliseconds < durationMs && !st.ObserveStopRequested)
            {
                var el = sw.ElapsedMilliseconds;

                if (!burnStarted && el >= BaseMs)
                {
                    burnStarted = true;
                    stage($"CPU 满载 {LoadMs / 1000} 秒（风扇会响，属正常）…");
                    Burn.Start(threads);
                }
                else if (burnStarted && !burnStopped && el >= stopBurnAt)
                {
                    burnStopped = true;
                    Burn.Stop();
                    stage("停载，等风扇降速…");
                }

                if (el - lastSample >= SampleMs)
                {
                    lastSample = el;
                    var frame = EcSampling.StatusFrame(st);
                    if (frame is not null)
                    {
                        var (cpuW, cpuT, gpuT) = EcSampling.Refs(st, EcSampling.Snapshot(st));
                        var s = new LayoutSample(cpuW ?? double.NaN, cpuT ?? double.NaN, gpuT ?? double.NaN, frame);
                        if (el < BaseMs) baseline.Add(s);
                        else if (el < stopBurnAt) loaded.Add(s);
                        else recovery.Add(s);
                    }
                }

                Thread.Sleep(20);
            }
        }
        finally
        {
            Burn.Stop();
            if (fanWasRunning)
            {
                try { st.Fan.Start(); } catch { /* 恢复失败留在日志，别让采集白跑 */ }
            }
        }

        var json = new JsonObject
        {
            ["attempted"] = true,
            ["seconds"] = TotalSeconds,
            ["load_seconds"] = LoadMs / 1000,
            ["threads"] = threads,
            ["samples"] = new JsonObject
            {
                ["baseline"] = baseline.Count,
                ["load"] = loaded.Count,
                ["recovery"] = recovery.Count,
            },
            ["cpu_power_w"] = new JsonObject
            {
                ["baseline"] = Round(Mean(baseline, s => s.CpuPowerW)),
                ["load"] = Round(Mean(loaded, s => s.CpuPowerW)),
            },
            ["cpu_temp_c"] = new JsonObject
            {
                ["baseline"] = Round(Mean(baseline, s => s.CpuTempC)),
                ["load"] = Round(Mean(loaded, s => s.CpuTempC)),
            },
        };

        return new Result(json, baseline, loaded, recovery, true, null);
    }

    /// <summary>这一段没做成（连不上 EC / 没 status 命令）。留下的是一句具体原因，不是一个空对象。</summary>
    private static Result Skipped(string why)
        => new(new JsonObject { ["attempted"] = false, ["skip"] = why }, [], [], [], false, why);

    private static double? Mean(IReadOnlyList<LayoutSample> samples, Func<LayoutSample, double> pick)
    {
        var vals = samples.Select(pick).Where(v => !double.IsNaN(v)).ToList();
        return vals.Count == 0 ? null : vals.Average();
    }

    private static JsonNode? Round(double? v) => v is null ? null : JsonValue.Create(Math.Round(v.Value, 1));

    /// <summary>
    /// 纯计算线程：每个线程在自己的循环里做浮点运算，直到 <see cref="Stop"/> 被调到。
    ///
    /// 优先级给 BelowNormal：采样本体和界面不能被它抢成幻灯片；
    /// 结果写进静态字段是**防优化**用的 —— 不留下副作用，JIT 有权把整个循环删掉。
    /// </summary>
    private static class Burn
    {
        private static volatile bool _stop = true;
        private static long _sink;

        public static void Start(int threads)
        {
            _stop = false;
            for (var i = 0; i < threads; i++)
            {
                var t = new Thread(() =>
                {
                    double x = 1.0;
                    while (!_stop)
                        for (var k = 0; k < 20000; k++) x = Math.Sqrt(x * 1.0000001 + k);
                    Interlocked.Exchange(ref _sink, BitConverter.DoubleToInt64Bits(x));
                })
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                };
                t.Start();
            }
        }

        public static void Stop() => _stop = true;
    }
}
