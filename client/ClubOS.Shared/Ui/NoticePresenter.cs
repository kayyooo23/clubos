namespace ClubOS.Shared.Ui;

public enum NoticeStyle
{
    Corner,        // маленькое окно в углу поверх всех окон (как предупреждения о времени)
    FullScreen,    // окно на весь экран с кнопкой «Понятно»: поверх полноэкранной игры уголок не виден
}

/// <summary>
/// Как показать сообщение администратора. В углу его видно поверх меню ClubOS, обычных окон и игр «в окне без рамки». Поверх игры в «настоящем»
/// полноэкранном режиме маленькое окно не видно (Windows отдаёт игре весь экран), а сообщение теряться не должно (гость шумит, администратор
/// не может подойти). Поэтому, если на переднем плане чужое полноэкранное приложение, сообщение выходит на весь экран и забирает фокус:
/// игра сворачивается, гость читает и нажимает «Понятно», игра возвращается.
/// Лишний раз прервать игру лучше, чем потерять сообщение: любое полноэкранное приложение (в том числе игра в «безрамочном» режиме) считаем полноэкранным.
/// </summary>
public static class NoticePresenter
{
    // значения SHQueryUserNotificationState: 2 = полноэкранное приложение или режим презентации, 3 = полноэкранное приложение Direct3D
    private const int Busy = 2, Direct3DFullScreen = 3;

    public static NoticeStyle Choose(bool foregroundIsOurs, int notificationState) =>
        !foregroundIsOurs && notificationState is Busy or Direct3DFullScreen ? NoticeStyle.FullScreen : NoticeStyle.Corner;
}
