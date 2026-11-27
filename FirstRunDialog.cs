using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace ESCaptureSketch;

/// <summary>첫 실행에서 Windows 시작 시 자동 실행을 등록할지 묻는 창. 답하지 않고 닫으면 등록하지 않는다.</summary>
public static class FirstRunDialog
{
    public static Window Build()
    {
        var w = new Window
        {
            Title = "ESCaptureSketch", Width = 520, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true,   // 트레이 프로그램이라 앞에 띄우지 않으면 못 볼 수 있다
            FontFamily = new FontFamily("Segoe UI, Malgun Gothic"), FontSize = 14, Icon = AppIcon.Source, Background = ThemeService.WinBg,
        };
        ThemeService.Hook(w);

        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        panel.Children.Add(new TextBlock { Text = L.T("Windows를 시작할 때 ESCaptureSketch를 자동으로 실행할까요?"), TextWrapping = TextWrapping.Wrap, Foreground = ThemeService.Fg, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = L.T("나중에 설정 > 시스템·캡쳐에서 바꿀 수 있습니다."), TextWrapping = TextWrapping.Wrap, Foreground = ThemeService.SubFg, Margin = new Thickness(0, 8, 0, 18) });

        var yes = new Button { Content = L.T("자동 실행"), Padding = new Thickness(18, 5, 18, 5), Margin = new Thickness(0, 0, 8, 0) };
        var no = new Button { Content = L.T("하지 않음"), Padding = new Thickness(18, 5, 18, 5), IsCancel = true };
        yes.Click += (_, _) => w.DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        panel.Children.Add(buttons);
        w.Content = panel;
        return w;
    }

    /// <summary>"자동 실행"을 골랐으면 true.</summary>
    public static bool Ask() => Build().ShowDialog() == true;
}
