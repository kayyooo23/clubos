using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClubOS.Overlay;

public enum EdgeSide { Left, Right, Top, Bottom }

/// <summary>
/// Заливка свободных полей вокруг баннера. Рекламодатели присылают картинки разной формы, а экран 16 на 9: когда картинка уже или шире экрана, остаются
/// пустые поля (по бокам или сверху и снизу). Если у картинки гладкий край (сплошной фон или плавный переход цвета, как у большинства баннеров), берём
/// её крайний ряд пикселей и растягиваем в сторону: баннер выглядит продолженным на весь экран, шва нет, даже когда фон светлее с одного края и темнее с другого.
/// Если край пёстрый или на нём виден предмет (фото, текст, резкие детали), растяжка дала бы полосы, поэтому поля остаются тёмными (возвращается null).
/// Это растянутая картинка, а не градиентная кисть: градиентные кисти на части видеокарт рисуются с белыми полосами.
/// </summary>
public static class BannerEdges
{
    private const int EdgeWidth = 3;          // сколько крайних рядов усредняем в один (убирает шум JPEG)
    private const double MaxStep = 3.0;       // средний скачок цвета между соседними пикселями края (из 255): больше уже не гладкий край
    private const int MaxJump = 16;           // самый резкий скачок между соседними пикселями: плавный фон его не даёт, а предмет, касающийся края, даёт

    public static BitmapSource? Left(BitmapSource image) => Edge(image, EdgeSide.Left);

    public static BitmapSource? Right(BitmapSource image) => Edge(image, EdgeSide.Right);

    public static BitmapSource? Top(BitmapSource image) => Edge(image, EdgeSide.Top);

    public static BitmapSource? Bottom(BitmapSource image) => Edge(image, EdgeSide.Bottom);

    /// <summary>Левый и правый край, если картинка уже экрана (поля по бокам); верхний и нижний, если шире (поля сверху и снизу).</summary>
    public static (BitmapSource? First, BitmapSource? Second) For(BitmapSource image, bool bandsOnTopAndBottom) =>
        bandsOnTopAndBottom ? (Edge(image, EdgeSide.Top), Edge(image, EdgeSide.Bottom)) : (Edge(image, EdgeSide.Left), Edge(image, EdgeSide.Right));

    private static BitmapSource? Edge(BitmapSource source, EdgeSide side)
    {
        try
        {
            var image = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int width = image.PixelWidth, height = image.PixelHeight;
            if (width < 1 || height < 1) return null;
            var vertical = side is EdgeSide.Left or EdgeSide.Right;       // край идёт сверху вниз (левый, правый) или слева направо (верхний, нижний)
            var length = vertical ? height : width;
            var depth = Math.Min(EdgeWidth, vertical ? width : height);
            var rect = side switch
            {
                EdgeSide.Left => new Int32Rect(0, 0, depth, height),
                EdgeSide.Right => new Int32Rect(width - depth, 0, depth, height),
                EdgeSide.Top => new Int32Rect(0, 0, width, depth),
                _ => new Int32Rect(0, height - depth, width, depth),
            };
            var raw = new byte[rect.Width * rect.Height * 4];
            image.CopyPixels(rect, raw, rect.Width * 4, 0);

            // одна линия: среднее по крайним рядам в каждой точке вдоль края
            var line = new byte[length * 4];
            for (var i = 0; i < length; i++)
                for (var channel = 0; channel < 4; channel++)
                {
                    var sum = 0;
                    for (var d = 0; d < depth; d++)
                    {
                        var x = vertical ? d : i;
                        var y = vertical ? i : d;
                        sum += raw[(y * rect.Width + x) * 4 + channel];
                    }
                    line[i * 4 + channel] = (byte)(sum / depth);
                }

            for (var i = 0; i < length; i++)
                if (line[i * 4 + 3] < 200) return null;   // у прозрачного края цвета нет

            if (length > 1)
            {
                // насколько в среднем меняется цвет от точки к точке; плавный переход даёт малое число, пёстрый край большое
                var jumps = new double[3];
                for (var i = 1; i < length; i++)
                    for (var channel = 0; channel < 3; channel++)
                    {
                        var jump = Math.Abs(line[i * 4 + channel] - line[(i - 1) * 4 + channel]);
                        if (jump > MaxJump) return null;      // на краю виден предмет или текст: растянув его, получили бы полосу через всё поле
                        jumps[channel] += jump;
                    }
                if (jumps.Max() / (length - 1) > MaxStep) return null;
            }

            var result = vertical
                ? BitmapSource.Create(1, length, 96, 96, PixelFormats.Bgra32, null, line, 4)
                : BitmapSource.Create(length, 1, 96, 96, PixelFormats.Bgra32, null, line, length * 4);
            result.Freeze();
            return result;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or OverflowException or System.IO.IOException)
        {
            return null;
        }
    }
}
