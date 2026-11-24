using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QControl.App.Render;

/// <summary>
/// 把 <see cref="SoftCanvas"/>（预乘 BGRA）写盘成 PNG。
///
/// 只在**开发用截图**路径调用（<c>--tray-menu-shot</c> / <c>--osd-shot</c>），
/// 正常启动一行都不碰 —— 这里一 import <c>PngBitmapEncoder</c> 就会把 WPF 的
/// 图像编码器拉进进程，托盘常驻态的 77 MB 就保不住了。
/// </summary>
internal static class PngSave
{
    public static void FromCanvas(SoftCanvas c, string path)
    {
        var src = BitmapSource.Create(c.Width, c.Height, 96, 96,
                                      PixelFormats.Pbgra32, null, c.Buffer, c.Width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
