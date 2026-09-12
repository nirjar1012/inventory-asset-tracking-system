using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using YardTracker.Station.ViewModels;

namespace YardTracker.Station.Views;

public partial class ScanView : UserControl
{
    public ScanView()
    {
        InitializeComponent();

        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ScanViewModel old)
                old.FocusScanRequested -= FocusScanBox;
            if (e.NewValue is ScanViewModel current)
                current.FocusScanRequested += FocusScanBox;
        };
        Loaded += (_, _) => FocusScanBox();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                FocusScanBox();
        };
    }

    private void FocusScanBox() =>
        Dispatcher.BeginInvoke(() =>
        {
            Keyboard.Focus(ScanBox);
            ScanBox.CaretIndex = ScanBox.Text.Length;
        }, DispatcherPriority.Input);

    /// <summary>
    /// A keyboard-wedge scanner types into whatever control has focus. If the operator last tapped a
    /// mode tile or dropdown, route the characters into the scan box instead of losing the read.
    /// </summary>
    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        if (e.OriginalSource is not TextBox && e.Text.Length > 0 && !char.IsControl(e.Text[0]))
        {
            ScanBox.Text += e.Text;
            Keyboard.Focus(ScanBox);
            ScanBox.CaretIndex = ScanBox.Text.Length;
            e.Handled = true;
        }
        base.OnPreviewTextInput(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Return && e.OriginalSource is not TextBox && ScanBox.Text.Length > 0
            && DataContext is ScanViewModel vm && vm.SubmitScanCommand.CanExecute(null))
        {
            vm.SubmitScanCommand.Execute(null);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }
}
