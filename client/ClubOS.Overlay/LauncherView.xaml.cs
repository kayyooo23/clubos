using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClubOS.Shared.Ui;

namespace ClubOS.Overlay;

/// <summary>
/// Меню гостя: игры, программы, тарифы, баланс и сессия. Одно и то же представление живёт в двух окнах:
/// на экране блокировки (гость вошёл, но сессию ещё не начал) и в окне LauncherWindow (пока он играет).
/// </summary>
public partial class LauncherView : UserControl
{
    private LauncherModel? _model;

    public LauncherView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_model is not null) _model.Flow.PropertyChanged -= OnFlowChanged;
        _model = e.NewValue as LauncherModel;
        if (_model is not null)
        {
            _model.Flow.PropertyChanged += OnFlowChanged;
            RecolorClock();
        }
    }

    private void OnFlowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FlowController.SessionClock)) RecolorClock();
    }

    /// <summary>Последние 5 минут янтарным, последняя минута красным: гость успеет сохраниться или пополнить баланс.</summary>
    private void RecolorClock()
    {
        var left = _model?.Flow.Snapshot.SecondsLeft;
        HeaderClock.Foreground = (Brush)FindResource(left is null or > 300 ? "Text" : left > 60 ? "Warn" : "Danger");
    }

    // ---------- ввод: пока гость что-то делает, вход в кабинете не истекает ----------
    private void View_Input(object sender, RoutedEventArgs e) => _model?.Flow.Touch();

    // ---------- кнопки ----------
    private static async void Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Log.Error("Сбой в обработчике кнопки меню", ex); }
    }

    private void Tariff_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        var id = (int)((Button)sender).Tag;
        if (!_model.Flow.HasSession)
        {
            Run(() => _model.Flow.StartAsync(id));
            return;
        }
        // во время сессии та же карточка продлевает её: время добавляется, цена списывается сразу
        var name = _model.Flow.ExtendTariffs.FirstOrDefault(t => t.Id == id)?.Name ?? "пакет";
        Run(async () => { if (await _model.Flow.ExtendAsync(id)) _model.ShowNotice($"Время добавлено: {name}"); });
    }

    private void Extend_Click(object sender, RoutedEventArgs e) => _model?.SelectPage(LauncherPage.Tariffs);

    private void TopUp_Click(object sender, RoutedEventArgs e)
    {
        if (_model is not null) Run(() => _model.Bar.TopUpAsync());
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        if (!ConfirmWindow.Ask(Window.GetWindow(this), "Завершить сессию?", "Оставшееся время пакета не возвращается.", "Завершить", "Остаться")) return;
        Run(_model.Flow.StopAsync);
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (_model is not null) Run(_model.Flow.LogoutAsync);
    }

    private void App_Click(object sender, RoutedEventArgs e)
    {
        if (_model is not null && ((FrameworkElement)sender).DataContext is AppCard card) _model.Launch(card);
    }

    private void Product_Click(object sender, RoutedEventArgs e)
    {
        if (_model is not null && ((FrameworkElement)sender).DataContext is ProductCard card) _model.Bar.Add(card.Id);
    }

    private void CartPlus_Click(object sender, RoutedEventArgs e) => _model?.Bar.Add((int)((Button)sender).Tag);
    private void CartMinus_Click(object sender, RoutedEventArgs e) => _model?.Bar.Remove((int)((Button)sender).Tag);

    private void Order_Click(object sender, RoutedEventArgs e)
    {
        if (_model is not null) Run(() => _model.Bar.PlaceOrderAsync());
    }

    private void CancelOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        var id = (int)((Button)sender).Tag;
        Run(() => _model.Bar.CancelOrderAsync(id));
    }

    private void Call_Click(object sender, RoutedEventArgs e)
    {
        if (_model is not null) Run(() => _model.Bar.CallAdminAsync());
    }

    private void Chip_Click(object sender, RoutedEventArgs e) =>
        _model?.SelectCategory((string)((Button)sender).Tag);
}
