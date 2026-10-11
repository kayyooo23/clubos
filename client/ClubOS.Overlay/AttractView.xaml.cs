using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ClubOS.Shared.Ui;

namespace ClubOS.Overlay;

/// <summary>Заставка на весь экран: показывает картинку текущего баннера, плавно меняет её на следующую и рисует точки карусели.</summary>
public partial class AttractView : UserControl
{
    private static readonly Duration Fade = new(TimeSpan.FromMilliseconds(700));
    private const int MaxDecodeWidth = 3840;   // шире экрана 4K картинку в памяти держать незачем (до 4096 пикселей разрешено загружать)

    /// <summary>Один из двух слоёв: две заливки полей (слева и справа или сверху и снизу) плюс сама картинка.</summary>
    private sealed record Layer(Grid Root, Image Fill1, Image Fill2, Image Picture)
    {
        public BitmapSource? Loaded { get; set; }
    }

    private readonly Layer _a, _b;
    private AttractModel? _model;
    private AdSlide? _shown;
    private Layer? _front;   // какой из двух слоёв сейчас сверху

    public AttractView()
    {
        InitializeComponent();
        _a = new Layer(LayerA, FillA1, FillA2, ImageA);
        _b = new Layer(LayerB, FillB1, FillB2, ImageB);
        DataContextChanged += OnDataContextChanged;
        SizeChanged += (_, _) => { foreach (var layer in new[] { _a, _b }) if (layer.Loaded is not null) PlaceFills(layer); };
    }

    /// <summary>Какой баннер на экране сейчас (для проверки в тестах).</summary>
    public AdSlide? Shown => _shown;

    private AdBannerModel? Ads => _model?.Ads;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_model is not null) _model.Ads.PropertyChanged -= OnAdsChanged;
        _model = e.NewValue as AttractModel;
        if (_model is not null) _model.Ads.PropertyChanged += OnAdsChanged;
        Show(Ads?.Current);
        RebuildDots();
    }

    private void OnAdsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AdBannerModel.Current)) { Show(Ads?.Current); RebuildDots(); }
        else if (e.PropertyName == nameof(AdBannerModel.Slides)) RebuildDots();
    }

    private void Show(AdSlide? slide)
    {
        if (Equals(slide, _shown)) return;
        if (slide is null)
        {
            _shown = null;
            foreach (var layer in new[] { _a, _b })
            {
                layer.Root.BeginAnimation(OpacityProperty, null);
                layer.Picture.Source = layer.Fill1.Source = layer.Fill2.Source = null;
                layer.Loaded = null;
                layer.Root.Opacity = 0;
            }
            Meta.Text = "";
            return;
        }

        var loaded = Load(slide);
        if (loaded is null)
        {
            Ads?.MarkBroken(slide);   // повреждённую картинку из очереди убираем, модель выберет следующую
            return;
        }

        // новый слой ложится сверху и проявляется, прежний после этого гаснет
        var incoming = ReferenceEquals(_front, _a) ? _b : _a;
        var outgoing = ReferenceEquals(incoming, _a) ? _b : _a;
        incoming.Picture.Source = loaded;
        incoming.Loaded = loaded;
        PlaceFills(incoming);
        Panel.SetZIndex(incoming.Root, 1);
        Panel.SetZIndex(outgoing.Root, 0);
        incoming.Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Fade));
        if (outgoing.Picture.Source is not null) outgoing.Root.BeginAnimation(OpacityProperty, new DoubleAnimation(outgoing.Root.Opacity, 0, Fade) { BeginTime = Fade.TimeSpan });
        _front = incoming;
        _shown = slide;
        Meta.Text = slide.Meta;
        Caption.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Fade));   // подпись нового баннера проявляется вместе с ним
    }

    /// <summary>
    /// Заливка свободных полей: если картинка шире экрана (по пропорции), пустые полосы сверху и снизу, иначе слева и справа.
    /// Пересчитывается при смене размера окна: пропорция экрана может поменяться.
    /// </summary>
    private void PlaceFills(Layer layer)
    {
        if (layer.Loaded is not { } image) return;
        var screenRatio = ActualWidth > 0 && ActualHeight > 0 ? ActualWidth / ActualHeight : 16.0 / 9;
        var imageRatio = (double)image.PixelWidth / image.PixelHeight;
        var bandsOnTopAndBottom = imageRatio > screenRatio;
        var (first, second) = BannerEdges.For(image, bandsOnTopAndBottom);
        layer.Fill1.Source = first;
        layer.Fill2.Source = second;
        Grid.SetColumn(layer.Fill1, 0);
        Grid.SetRow(layer.Fill1, 0);
        if (bandsOnTopAndBottom)
        {
            Grid.SetColumnSpan(layer.Fill1, 2); Grid.SetRowSpan(layer.Fill1, 1);
            Grid.SetColumn(layer.Fill2, 0); Grid.SetRow(layer.Fill2, 1);
            Grid.SetColumnSpan(layer.Fill2, 2); Grid.SetRowSpan(layer.Fill2, 1);
        }
        else
        {
            Grid.SetColumnSpan(layer.Fill1, 1); Grid.SetRowSpan(layer.Fill1, 2);
            Grid.SetColumn(layer.Fill2, 1); Grid.SetRow(layer.Fill2, 0);
            Grid.SetColumnSpan(layer.Fill2, 1); Grid.SetRowSpan(layer.Fill2, 2);
        }
    }

    /// <summary>Точки карусели: по одной на баннер, текущая золотая. Одна точка не рисуется: листать нечего.</summary>
    private void RebuildDots()
    {
        Dots.Children.Clear();
        if (Ads is not { } ads || ads.Slides.Count < 2) return;
        foreach (var slide in ads.Slides)
        {
            var active = ads.Current is { } current && current.Id == slide.Id && current.Version == slide.Version;
            Dots.Children.Add(new Ellipse
            {
                Width = 10, Height = 10, Margin = new Thickness(6, 0, 6, 0),
                Fill = (Brush)Application.Current.Resources[active ? "Accent" : "Muted"],
            });
        }
    }

    /// <summary>Сколько точек сейчас нарисовано (для проверки в тестах).</summary>
    public int DotCount => Dots.Children.Count;

    /// <summary>Читает файл сразу целиком (OnLoad): служба потом может заменить или удалить файл, а картинка на экране не должна от этого пропасть.</summary>
    private static BitmapImage? Load(AdSlide slide)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(slide.Path, UriKind.Absolute);
            if (slide.Width > MaxDecodeWidth) image.DecodePixelWidth = MaxDecodeWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or InvalidOperationException or UriFormatException or ArgumentException or FormatException)
        {
            Log.Error($"Баннер {slide.Id} не открылся", e);
            return null;
        }
    }
}
