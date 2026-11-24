using QControl.Core.Startup;

namespace QControl.Tests;

/// <summary>
/// 自我提权的决策表。这些用例挡的是两类事故：
///   · 只读诊断（截图回归、契约自检）开始弹 UAC —— 自动化跑不起来，人也没空点；
///   · 提权时参数被吃掉或拼错 —— 新进程起来后路径不对，表现为"双击闪一下就没了"。
/// </summary>
public class ElevationPlanTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--show")]
    [InlineData("--selftest")]
    [InlineData("--ui-shot")]
    [InlineData("--ui-actions-selftest")]
    [InlineData("--ui-fit-selftest")]
    [InlineData("--autostart-selftest")]
    [InlineData("--no-elevate")]
    [InlineData("--elevated")]
    public void 只读模式不得触发提权(string flag)
    {
        Assert.False(ElevationPlan.WantsElevation([flag]));
        // 混在别的参数里也一样：位置无关
        Assert.False(ElevationPlan.WantsElevation(["--port", "8765", flag, "--tray"]));
    }

    [Theory]
    [InlineData("--tray")]
    [InlineData("--no-tray")]
    [InlineData("--gpu-oc-selftest")]   // 要写 NVML：不提权等于验了个假的
    public void 日常启动与要写硬件的自检必须提权(string flag)
        => Assert.True(ElevationPlan.WantsElevation([flag]));

    [Fact]
    public void 无参数启动也要提权()
        => Assert.True(ElevationPlan.WantsElevation([]));

    [Fact]
    public void 提权时参数原样带走并在末尾加标记()
    {
        var args = ElevationPlan.ElevatedArguments(["--page", "perf", "--port", "9000"]);
        Assert.Equal(new[] { "--page", "perf", "--port", "9000", "--elevated" }, args);
    }

    [Fact]
    public void 已有的提权标记要被剔掉再加()
    {
        // 否则反复重启会积一串 --elevated（虽然不致命，但日志里会看不出来是谁加的）
        var args = ElevationPlan.ElevatedArguments(["--elevated", "--tray"]);
        Assert.Equal(new[] { "--tray", "--elevated" }, args);
        Assert.Single(args, a => a == "--elevated");
    }

    [Theory]
    [InlineData("--tray", "--tray")]                       // 不需要引号就别加
    [InlineData(@"C:\ui\shot.png", @"C:\ui\shot.png")]      // 没空格同理
    [InlineData("", "\"\"")]                                // 空串必须显式给一对引号
    [InlineData(@"C:\a b\c.png", "\"C:\\a b\\c.png\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]                      // 内嵌引号要转义
    [InlineData(@"a b\", "\"a b\\\\\"")]                    // 结尾反斜杠要翻倍，不然吃掉收尾引号
    public void 参数加引号遵守_Windows_规则(string arg, string expected)
        => Assert.Equal(expected, ElevationPlan.Quote(arg));

    [Fact]
    public void 拼接后的命令行保留原始参数()
    {
        var line = ElevationPlan.Join([@"C:\Users\me\My Shot.png", "--theme", "dark"]);
        Assert.Equal("\"C:\\Users\\me\\My Shot.png\" --theme dark", line);
    }

    // ---------------------------------------------------------------- 就地交棒（界面按钮）

    [Fact]
    public void 界面提权重启要去掉托盘参数并加上接管标记()
    {
        var args = ElevationPlan.TakeoverArguments(
            ["--tray", "--port", "8765", "--no-open", "--elevated"]);

        // --tray/--no-open 必须去掉：用户正看着窗口，新进程缩进托盘等于"没反应"
        // --elevated 先去重再加（只留一个），--takeover 告诉新进程去接管端口
        Assert.Equal(new[] { "--port", "8765", "--elevated", "--takeover" }, args);
    }

    [Fact]
    public void 只读实例点提权要把_no_elevate_丢掉()
    {
        // 只读实例本来就是 --no-elevate 起的；照抄下去新进程会被判成"别提权"，
        // 点一次按钮还是只读 —— 这个按钮就等于坏的
        var args = ElevationPlan.TakeoverArguments(["--no-elevate", "--port", "8765"]);

        Assert.DoesNotContain("--no-elevate", args);
        Assert.Equal(new[] { "--port", "8765", "--elevated", "--takeover" }, args);
        // 而且新进程必须是"不会再提权一次"的状态（它已经是管理员了）
        Assert.False(ElevationPlan.WantsElevation(args));
    }

    [Fact]
    public void 接管参数不会再触发第二次提权()
        => Assert.False(ElevationPlan.WantsElevation(
            ElevationPlan.TakeoverArguments(["--tray"])));

    [Fact]
    public void 手动带接管标记仍然要提权()
        // 有人在只读实例的终端里手打 --takeover：这就是"我要管理员接管"的意思
        => Assert.True(ElevationPlan.WantsElevation(["--takeover"]));

    [Fact]
    public void 提权重启按当前页重新给_page()
    {
        // 旧 --page 必须连值一起丢掉：Program 用 Array.IndexOf 取第一个，
        // 留着旧的就会跳回上一页，用户以为"刚才那页丢了"
        var args = ElevationPlan.TakeoverArguments(["--page", "perf", "--tray"], page: "cool");
        Assert.Equal(new[] { "--page", "cool", "--elevated", "--takeover" }, args);
    }

    [Fact]
    public void 不指定页时不该凭空塞一个_page()
        => Assert.DoesNotContain("--page", ElevationPlan.TakeoverArguments(["--page", "perf"]));
}
