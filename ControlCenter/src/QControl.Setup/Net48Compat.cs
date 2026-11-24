namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// net48 的 BCL 里没有这个类型，但 C# 9 之后 `init` 访问器 / `record` 要靠它编译
    /// （编译器会往 setter 的 modreq 里写这个名字）。**纯编译期标记**，删了编译不过，
    /// 留着运行时不参与任何逻辑。
    ///
    /// 为什么不去追 .NET Core 的源码把它补全：它本来就没有成员，只有名字有意义。
    /// </summary>
    internal static class IsExternalInit
    {
    }
}

namespace QControl.Setup
{
    using System;
    using System.IO;

    /// <summary>
    /// net48 缺失、但代码里已经用了的小工具。
    ///
    /// 有这些就不必为了"迁回 .NET Framework"把主逻辑改写成另一种风格 ——
    /// 安装器的代码与 net10 时代保持同一份，只是底下多一层垫片。
    /// </summary>
    internal static class Compat
    {
        /// <summary>
        /// 等价于 .NET Core 的 <c>Path.GetRelativePath</c>（net48 没有）。
        ///
        /// 用 Uri 做基准是最省事的可靠做法：两个绝对路径都变成 file:// URI 之后，
        /// `MakeRelativeUri` 就是现成的相对路径算法（跨盘符时它会返回绝对路径，正好也是我们要的）。
        /// </summary>
        public static string GetRelativePath(string relativeTo, string path)
        {
            var from = Path.GetFullPath(relativeTo);
            var to = Path.GetFullPath(path);

            if (!from.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                from += Path.DirectorySeparatorChar;

            var fromUri = new Uri(from);
            var toUri = new Uri(to);
            if (!string.Equals(fromUri.Scheme, toUri.Scheme, StringComparison.OrdinalIgnoreCase)) return to;

            var rel = Uri.UnescapeDataString(fromUri.MakeRelativeUri(toUri).ToString());
            return rel.Replace('/', Path.DirectorySeparatorChar);
        }

        /// <summary>等价于 .NET Core 的 <c>Path.IsPathFullyQualified</c>（net48 没有）。</summary>
        public static bool IsPathFullyQualified(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (!Path.IsPathRooted(path)) return false;
            // `\\server\share` 是 UNC，算完整路径；单个 `\foo` 是「当前盘根相对」，不算。
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            return !path.StartsWith(@"\", StringComparison.Ordinal);
        }
    }
}
