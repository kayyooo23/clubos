using ClubOS.Shared.Ui;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ClubOS.Overlay;

/// <summary>Значок в трее на время, пока на ПК работает сотрудник: единственный способ вернуть ПК на экран блокировки.</summary>
public sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayController(FlowController flow)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripLabel("ClubOS: режим сотрудника") { Enabled = false });
        menu.Items.Add("Заблокировать ПК", null, async (_, _) =>
        {
            try { await flow.LogoutAsync(); } catch (Exception e) { Log.Error("Не удалось заблокировать ПК", e); }
        });
        _icon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Shield,
            Text = "ClubOS: режим сотрудника. Правая кнопка мыши, чтобы заблокировать ПК",
            ContextMenuStrip = menu,
            Visible = false,
        };
    }

    public void Show()
    {
        if (_icon.Visible) return;
        _icon.Visible = true;
        _icon.ShowBalloonTip(5000, "ClubOS", "Режим сотрудника. Чтобы заблокировать ПК, щёлкните по этому значку правой кнопкой.", Forms.ToolTipIcon.Info);
    }

    public void Hide() => _icon.Visible = false;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
