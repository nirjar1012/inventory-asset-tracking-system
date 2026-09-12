using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace YardTracker.Station.Views;

public partial class SignInView : UserControl
{
    public SignInView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                Dispatcher.BeginInvoke(() => Keyboard.Focus(BadgeBox), DispatcherPriority.Input);
        };
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => Keyboard.Focus(BadgeBox), DispatcherPriority.Input);
    }
}
