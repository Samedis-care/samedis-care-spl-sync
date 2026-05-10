using System.Windows;

namespace SamedisCare.SplSync.Tray.Dialogs;

public partial class LockDialog : Window
{
    public LockDialog() => InitializeComponent();

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
