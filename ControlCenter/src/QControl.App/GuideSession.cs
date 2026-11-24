using System.Text.Json.Nodes;
using QControl.Core.Devices;

namespace QControl.App;

/// <summary>
/// 引导会话处在哪一段。
///
/// 为什么要分两段（2026-11-14）：点「一键采集」之后，前面还有 30 多秒在
/// 读机型 / 采样 16 帧 / 读显卡模式 —— 那段时间旧实现**什么都没有**，用户以为
/// "程序还没开始"就去干别的；等小窗突然冒出来，他早不在状态了。现在小窗在点下按钮
/// 的那一刻就浮出来（<see cref="GuidePhase.Warmup"/>），把当前阶段如实打在上面，
/// 采样跑完再切到 <see cref="GuidePhase.Observing"/> 才开始计步、才允许标步骤。
///
/// ⚠️ Warmup 段**不许**接受标记：那时观察还没开始，标记的时间戳会落在窗口之外，
///    切出来的时间窗一定是错的（见 <see cref="GuideSession.Mark"/>）。
/// </summary>
internal enum GuidePhase
{
    /// <summary>没在采（小窗该收掉）。</summary>
    Idle,
    /// <summary>采集前段：小窗已浮出，但还没开始观察，先别动原厂。</summary>
    Warmup,
    /// <summary>观察原厂：能标步骤、能数写帧、有倒计时。</summary>
    Observing,
}

/// <summary>
/// 「引导适配」的会话状态：现在该做第几步、这一步抓到了几条、用户点了哪些标记。
///
/// 为什么状态要放在 App 层、而不是推断层：推断（<see cref="AdoptGuide"/>）是**纯函数**，
/// 给它一串带时间戳的帧和标记就能算结论，可以离线单测；而"当前第几步"是活的界面状态，
/// 会同时被三条线读：观察线程（计数）、界面 300ms 轮询（渲染）、采集收尾（取标记）。
/// 三者之间只靠这个对象加锁互斥，不互相回调 —— 观察线程不能碰 UI，UI 也不能等观察。
/// </summary>
internal sealed class GuideSession
{
    private readonly object _lock = new();
    private readonly List<GuideMark> _marks = [];
    private readonly int[] _frames = new int[AdoptGuide.Steps.Count];

    private DateTimeOffset _started = DateTimeOffset.Now;
    private int _index;
    private bool _active;
    private GuidePhase _phase = GuidePhase.Idle;

    /// <summary>观察窗口的截止时刻（UTC）。<see cref="DateTimeOffset.MinValue"/> = 还没开始观察。</summary>
    private DateTimeOffset _deadline = DateTimeOffset.MinValue;

    /// <summary>采集前段的实时阶段（"采样 EC 帧 7/16…"），Warmup 段小窗照着念。</summary>
    private string _stage = "";

    /// <summary>观察是否在跑（界面据此决定要不要显示引导面板）。</summary>
    public bool Active
    {
        get { lock (_lock) return _active; }
    }

    /// <summary>当前处在哪一段（小窗据此决定摆步骤还是摆"正在采样"）。</summary>
    public GuidePhase Phase
    {
        get { lock (_lock) return _phase; }
    }

    /// <summary>
    /// 当前这一步关心的是哪类操作（没在跑 / 做完了返回 null）。
    ///
    /// 观察线程拿它决定"这一下要不要顺带采一帧 status"：只有「风扇」那一步需要 ——
    /// 用户正在那一步把风扇拉起来，那一小段里转速才有变化，而转速只能靠"变速的形状"认
    /// （见 <see cref="QControl.Core.Devices.FanLayoutAdvisor"/>）。其它步骤采了也没用，
    /// 反而会和抓原厂写帧抢同一个缓冲区。
    /// </summary>
    public GuideStepKind? CurrentKind
    {
        get
        {
            lock (_lock)
                return _active && _index < AdoptGuide.Steps.Count ? AdoptGuide.Steps[_index].Kind : null;
        }
    }

    /// <summary>观察开始时刻 —— 第一个标记之前的帧算第一步的。</summary>
    public DateTimeOffset Started
    {
        get { lock (_lock) return _started; }
    }

    /// <summary>
    /// 采集一开始就先把小窗叫起来（Warmup 段）：用户在采集前段的 30 多秒里
    /// 能看到"程序在忙什么、什么时候轮到他"，而不是对着一个没反应的按钮等。
    /// 这一步**不**开始计时、**不**允许标记 —— 观察真开始时由 <see cref="Start"/> 接过去。
    /// </summary>
    public void BeginWarmup()
    {
        lock (_lock)
        {
            _marks.Clear();
            Array.Clear(_frames);
            _index = 0;
            _started = DateTimeOffset.Now;
            _deadline = DateTimeOffset.MinValue;
            _stage = "";
            _phase = GuidePhase.Warmup;
            _active = true;
        }
    }

    /// <summary>这一轮观察开始：清空上一轮的标记与计数。</summary>
    public void Start(DateTimeOffset at)
    {
        lock (_lock)
        {
            _marks.Clear();
            Array.Clear(_frames);
            _index = 0;
            _started = at;
            _deadline = DateTimeOffset.MinValue;    // 由 SetDeadline 紧接着补上
            _phase = GuidePhase.Observing;
            _active = true;
        }
    }

    /// <summary>观察结束（提前结束也算）。之后 <see cref="Note"/> 不再计数。</summary>
    public void End()
    {
        lock (_lock)
        {
            _active = false;
            _phase = GuidePhase.Idle;
        }
    }

    /// <summary>
    /// 观察窗口的截止时刻。小窗靠它算"还剩几秒" —— 只报真数：由观察循环把**实际**
    /// deadline 交进来，延长时再交一次（见 MachineObserve 的延长分支）。
    /// </summary>
    public void SetDeadline(DateTimeOffset deadline)
    {
        lock (_lock) _deadline = deadline;
    }

    /// <summary>采集前段的实时阶段（Warmup 段小窗照着念；观察开始后不再显示）。</summary>
    public void NoteStage(string? stage)
    {
        lock (_lock) _stage = stage ?? "";
    }

    /// <summary>
    /// 观察线程每抓到一条**新的**原厂写命令就调一次。只计数，不解析 ——
    /// 解析要等标记都定了才能做（见 <see cref="AdoptGuide.Analyze"/>）。
    ///
    /// ⚠️ 只数当前步骤关心的那条子命令：原厂的风扇自动曲线（0x0205）每隔几秒
    /// 就自己写一帧，若不加区分，"切性能档 / 改键盘灯"这几步会平白显示"抓到 1 条"，
    /// 用户以为操作被抓到就点了「完成这步」，收尾才发现这一档的证据是空的。
    /// </summary>
    public void Note(ushort gwf1)
    {
        lock (_lock)
        {
            if (!_active || _index >= _frames.Length) return;
            if (!AdoptGuide.CountsForStep(_index, gwf1)) return;
            _frames[_index]++;
        }
    }

    /// <summary>
    /// 记下"这一步做完了"（<paramref name="skip"/> = 这台没有这项），前进到下一步。
    /// 返回 true 表示引导步骤都走完了 —— 界面据此自动收尾，不让用户干等剩下的观察时间。
    /// </summary>
    public bool Mark(bool skip)
    {
        lock (_lock)
        {
            // 采集前段（Warmup）不许标：观察还没开始，标记时间会落在第一个时间窗之外，
            // 收尾切窗时那一步等于白标。小窗那边这两个按钮也是灰的，这里是第二道闸。
            if (!_active || _phase != GuidePhase.Observing) return false;
            if (_index >= AdoptGuide.Steps.Count) return true;
            _marks.Add(new GuideMark(AdoptGuide.Steps[_index].Id, DateTimeOffset.Now, skip));
            _index++;
            return _index >= AdoptGuide.Steps.Count;
        }
    }

    /// <summary>这一轮的所有标记（采集收尾时取走，交给纯逻辑切窗）。</summary>
    public IReadOnlyList<GuideMark> Marks
    {
        get { lock (_lock) return _marks.ToList(); }
    }

    /// <summary>
    /// 退回上一步。用户在引导里点错、或者原厂那一下没写 EC（这一步抓到 0 条）时要能重做 ——
    /// 没有这条路，一次误点就把这一步的窗口废掉了，只能整轮重采（两分钟）。
    ///
    /// 退回时把这一步的计数清零：那些帧属于已经作废的那个窗口，留着会让界面显示
    /// "抓到 3 条"但其实一条都不算数。
    /// </summary>
    public bool Back()
    {
        lock (_lock)
        {
            if (!_active || _phase != GuidePhase.Observing || _marks.Count == 0) return false;
            _marks.RemoveAt(_marks.Count - 1);
            _index = Math.Max(0, _index - 1);
            _frames[_index] = 0;
            return true;
        }
    }

    /// <summary>
    /// 给界面看的快照。**只报真事**：抓了几条是数出来的，没有百分比 ——
    /// 那玩意编不出来，编出来就是骗人（与本项目其它进度显示同一条规矩）。
    ///
    /// 2026-11-14 加了三项，全是为了让"用户没操作"这件事在界面上有交代：
    ///   · <c>phase</c>    —— warmup（采集前段，先别动原厂）/ observe（该你做了）；
    ///   · <c>stage</c>    —— warmup 段正在干什么（后端实时阶段，原样念）；
    ///   · <c>remaining</c> —— 观察窗口还剩几秒（-1 = 还没开始）。到点就按"没做到"处理，
    ///     所以这个数必须摆在用户眼前，不能等结束了才说"你没做"。
    /// </summary>
    public JsonObject Snapshot()
    {
        lock (_lock)
        {
            var steps = new JsonArray();
            for (var i = 0; i < AdoptGuide.Steps.Count; i++)
            {
                var s = AdoptGuide.Steps[i];
                var mark = _marks.FirstOrDefault(m => m.Step == s.Id);
                steps.Add(new JsonObject
                {
                    ["id"] = s.Id,
                    ["title"] = s.Title,
                    ["frames"] = _frames[i],
                    ["done"] = mark is not null,
                    ["skipped"] = mark?.Skipped ?? false,
                    ["current"] = _active && i == _index,
                });
            }

            var observing = _active && _phase == GuidePhase.Observing;
            var current = observing && _index < AdoptGuide.Steps.Count ? AdoptGuide.Steps[_index] : null;

            // 剩余秒数按**实际 deadline** 算（延长后由 SetDeadline 重新交进来），
            // 不用"总时长 - 已过时间"倒推：那样延长一次就永远算不对。
            var remaining = -1;
            if (observing && _deadline != DateTimeOffset.MinValue)
                remaining = (int)Math.Max(0, Math.Round((_deadline - DateTimeOffset.UtcNow).TotalSeconds));

            return new JsonObject
            {
                ["active"] = _active,
                ["phase"] = !_active ? "idle" : observing ? "observe" : "warmup",
                ["stage"] = _active && !observing ? _stage : null,
                ["remaining"] = remaining,
                ["index"] = _index,
                ["total"] = AdoptGuide.Steps.Count,
                ["finished"] = observing && _index >= AdoptGuide.Steps.Count,
                ["title"] = current?.Title,
                ["hint"] = current?.Hint,
                ["frames"] = current is null ? 0 : _frames[_index],
                ["steps"] = steps,
            };
        }
    }
}
