using System.Windows;
using WindowManager.App.ViewModels;

namespace WindowManager.App;

public partial class BrowserProfileNameDialog : Window
{
    public BrowserProfileNameDialog(string? initialName = null)
    {
        InitializeComponent();
        ViewModel = new BrowserProfileNameDialogViewModel
        {
            ProfileName = initialName?.Trim() ?? string.Empty
        };
        DataContext = ViewModel;
        Loaded += (_, _) => ProfileNameTextBox.Focus();
    }

    public BrowserProfileNameDialogViewModel ViewModel { get; }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}

public sealed class BrowserProfileNameDialogViewModel : ViewModelBase
{
    private string _profileName = string.Empty;

    public string ProfileName
    {
        get => _profileName;
        set => SetProperty(ref _profileName, value);
    }
}
