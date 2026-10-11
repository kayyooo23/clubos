using System.ComponentModel;
using System.Windows;
using ClubOS.Shared.Ui;

namespace ClubOS.Overlay;

/// <summary>
/// Меню гостя в окне, пока идёт сессия: игры, программы, баланс. Это обычное окно, не блокировка: его можно свернуть,
/// а закрыть нельзя (закрытие превращается в сворачивание), чтобы гость не потерял меню случайным Alt+F4.
/// </summary>
public partial class LauncherWindow : Window
{
    public bool AllowClose { get; set; }

    public LauncherWindow(LauncherModel model)
    {
        InitializeComponent();
        View.DataContext = model;
        var area = SystemParameters.WorkArea;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = area.Left;
        Top = area.Top;
        Width = area.Width;
        Height = area.Height;
        model.AppLaunched += _ => Dispatcher.BeginInvoke(() => WindowState = WindowState.Minimized);   // игра на весь экран, меню не мешает
    }

    /// <summary>Показать меню и вывести его вперёд (в том числе из свёрнутого состояния).</summary>
    public void Bring()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            WindowState = WindowState.Minimized;
        }
        base.OnClosing(e);
    }
}
