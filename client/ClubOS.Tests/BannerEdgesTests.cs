using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClubOS.Overlay;

namespace ClubOS.Tests;

/// <summary>Заливка свободных полей рядом с баннером: гладкий край картинки продолжается в стороны, пёстрый край поле не красит.</summary>
public class BannerEdgesTests
{
    private static BitmapSource Picture(int width, int height, Func<int, int, (byte B, byte G, byte R, byte A)> pixel)
    {
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var (b, g, r, a) = pixel(x, y);
                var i = (y * width + x) * 4;
                data[i] = b; data[i + 1] = g; data[i + 2] = r; data[i + 3] = a;
            }
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, data, width * 4);
    }

    /// <summary>Цвет строки y у столбца края как (R, G, B).</summary>
    private static (byte R, byte G, byte B) At(BitmapSource column, int y)
    {
        var pixel = new byte[4];
        column.CopyPixels(new Int32Rect(0, y, 1, 1), pixel, 4, 0);
        return (pixel[2], pixel[1], pixel[0]);
    }

    [Fact]
    public void A_flat_background_continues_to_both_sides()
    {
        var image = Picture(120, 40, (_, _) => (0, 105, 255, 255));
        foreach (var column in new[] { BannerEdges.Left(image), BannerEdges.Right(image) })
        {
            Assert.NotNull(column);
            Assert.Equal(1, column!.PixelWidth);
            Assert.Equal(40, column.PixelHeight);
            Assert.Equal(((byte)255, (byte)105, (byte)0), At(column, 0));
            Assert.Equal(((byte)255, (byte)105, (byte)0), At(column, 39));
        }
    }

    [Fact]
    public void A_smooth_shading_is_kept_so_the_fill_has_no_seam_with_the_picture()
    {
        // фон светлее сверху и темнее снизу (как на многих баннерах): плоский цвет дал бы шов, растянутый край его не даёт
        var image = Picture(120, 240, (_, y) => ((byte)40, (byte)(170 - y / 4), (byte)40, (byte)255));
        var column = BannerEdges.Left(image)!;
        Assert.NotNull(column);
        Assert.Equal(((byte)40, (byte)170, (byte)40), At(column, 0));
        Assert.Equal(((byte)40, (byte)(170 - 59), (byte)40), At(column, 239));
    }

    [Fact]
    public void Each_side_takes_its_own_edge()
    {
        var image = Picture(120, 40, (x, _) => x < 60 ? ((byte)200, (byte)50, (byte)10, (byte)255) : ((byte)10, (byte)50, (byte)200, (byte)255));
        Assert.Equal(((byte)10, (byte)50, (byte)200), At(BannerEdges.Left(image)!, 5));
        Assert.Equal(((byte)200, (byte)50, (byte)10), At(BannerEdges.Right(image)!, 5));
    }

    [Fact]
    public void A_busy_edge_such_as_a_photo_or_text_is_not_stretched()
    {
        var random = new Random(3);
        var photo = Picture(120, 240, (_, _) => ((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)255));
        Assert.Null(BannerEdges.Left(photo));
        Assert.Null(BannerEdges.Right(photo));

        var stripes = Picture(120, 240, (_, y) => y % 2 == 0 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255));
        Assert.Null(BannerEdges.Left(stripes));

        var halves = Picture(120, 40, (x, y) => x < 3 ? (y < 20 ? ((byte)0, (byte)0, (byte)255, (byte)255) : ((byte)255, (byte)0, (byte)0, (byte)255)) : ((byte)0, (byte)105, (byte)255, (byte)255));
        Assert.Null(BannerEdges.Left(halves));                               // резкий переход посередине края
        Assert.NotNull(BannerEdges.Right(halves));                           // правый край гладкий: берётся независимо от левого
    }

    [Fact]
    public void An_object_touching_the_edge_is_not_smeared_across_the_field()
    {
        // зелёный фон, а в середине края упирается коричневая подушка: тянуть такой край нельзя
        var image = Picture(120, 240, (_, y) => y is >= 60 and < 140 ? ((byte)45, (byte)96, (byte)176, (byte)255) : ((byte)60, (byte)160, (byte)70, (byte)255));
        Assert.Null(BannerEdges.Right(image));
        Assert.Null(BannerEdges.Left(image));
    }

    [Fact]
    public void A_transparent_edge_has_nothing_to_stretch()
    {
        var image = Picture(120, 40, (x, _) => x < 60 ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)0, (byte)105, (byte)255, (byte)255));
        Assert.Null(BannerEdges.Left(image));
        Assert.NotNull(BannerEdges.Right(image));
    }

    [Fact]
    public void Slight_jpeg_noise_on_a_flat_edge_does_not_matter()
    {
        var random = new Random(7);
        var image = Picture(120, 240, (_, _) => ((byte)random.Next(0, 4), (byte)(105 + random.Next(-3, 4)), (byte)(255 - random.Next(0, 4)), (byte)255));
        var column = BannerEdges.Left(image);
        Assert.NotNull(column);
        var (r, g, b) = At(column!, 100);
        Assert.InRange(r, 250, 255);
        Assert.InRange(g, 100, 110);
        Assert.InRange(b, 0, 4);
    }

    [Fact]
    public void A_one_pixel_wide_or_high_image_still_works()
    {
        Assert.NotNull(BannerEdges.Left(Picture(1, 10, (_, _) => (0, 105, 255, 255))));
        Assert.NotNull(BannerEdges.Right(Picture(10, 1, (_, _) => (0, 105, 255, 255))));
    }

    [Fact]
    public void Top_and_bottom_edges_work_like_the_side_ones_for_a_picture_wider_than_the_screen()
    {
        // фон светлее слева и темнее справа вдоль верхнего и нижнего края
        var image = Picture(240, 40, (x, _) => ((byte)40, (byte)(170 - x / 4), (byte)40, (byte)255));
        foreach (var edge in new[] { BannerEdges.Top(image), BannerEdges.Bottom(image) })
        {
            Assert.NotNull(edge);
            Assert.Equal(240, edge!.PixelWidth);
            Assert.Equal(1, edge.PixelHeight);
            var pixel = new byte[4];
            edge.CopyPixels(new Int32Rect(239, 0, 1, 1), pixel, 4, 0);
            Assert.Equal((byte)(170 - 59), pixel[1]);
        }
        var objectOnTop = Picture(240, 40, (x, y) => y == 0 && x is >= 80 and < 160 ? ((byte)0, (byte)0, (byte)255, (byte)255) : ((byte)60, (byte)160, (byte)70, (byte)255));
        Assert.Null(BannerEdges.Top(objectOnTop));         // на верхнем краю виден предмет
        Assert.NotNull(BannerEdges.Bottom(objectOnTop));   // нижний гладкий
    }

    [Fact]
    public void For_picks_the_pair_by_where_the_free_fields_are()
    {
        var image = Picture(120, 40, (_, _) => (0, 105, 255, 255));
        var (sideA, sideB) = BannerEdges.For(image, bandsOnTopAndBottom: false);
        Assert.Equal((1, 40), (sideA!.PixelWidth, sideA.PixelHeight));
        Assert.Equal((1, 40), (sideB!.PixelWidth, sideB.PixelHeight));
        var (topA, bottomB) = BannerEdges.For(image, bandsOnTopAndBottom: true);
        Assert.Equal((120, 1), (topA!.PixelWidth, topA.PixelHeight));
        Assert.Equal((120, 1), (bottomB!.PixelWidth, bottomB.PixelHeight));
    }
}
