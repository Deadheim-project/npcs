using System.Windows;

namespace DeadheimLauncher.Views;

public partial class InputDialog : Window
{
    public string? ResponseText { get; private set; }

    public InputDialog(string message, string defaultValue)
    {
        InitializeComponent();
        MessageText.Text = message;
        ResponseBox.Text = defaultValue;
        ResponseBox.SelectAll();
        ResponseBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResponseText = ResponseBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
