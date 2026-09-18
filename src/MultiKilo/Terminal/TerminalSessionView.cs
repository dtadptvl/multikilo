using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Terminal.Wpf;

namespace MultiKilo.Terminal;

internal sealed class TerminalSessionView : Grid
{
    private readonly NativeConPtyConnection _connection;
    private bool _connected;

    public TerminalSessionView(NativeConPtyConnection connection)
    {
        _connection = connection;

        Terminal = new TerminalControl
        {
            AutoResize = true,
            Focusable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Contained);
        KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.Contained);

        Focusable = true;
        Children.Add(Terminal);

        Loaded += OnLoaded;
        GotFocus += (_, _) => Terminal.Focus();
    }

    public TerminalControl Terminal { get; }

    public void Disconnect()
    {
        if (!_connected)
        {
            return;
        }

        Terminal.Connection = null!;
        _connected = false;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_connected)
        {
            return;
        }

        Terminal.SetTheme(CreateTheme(), "Cascadia Mono", 13);
        Terminal.Connection = _connection;
        _connected = true;

        if (Terminal.Rows > 0 && Terminal.Columns > 0)
        {
            _connection.Resize((uint)Terminal.Rows, (uint)Terminal.Columns);
        }
    }

    private static TerminalTheme CreateTheme() =>
        new()
        {
            DefaultBackground = 0x0C0C0C,
            DefaultForeground = 0xCCCCCC,
            DefaultSelectionBackground = 0x777777,
            CursorStyle = CursorStyle.BlinkingBar,
            ColorTable =
            [
                0x0C0C0C, 0x1F0FC5, 0x0EA113, 0x009CC1,
                0xDA3700, 0x981788, 0xDD963A, 0xCCCCCC,
                0x767676, 0x5648E7, 0x0CC616, 0xA5F1F9,
                0xFF783B, 0x9E00B4, 0xD6D661, 0xF2F2F2
            ]
        };
}
