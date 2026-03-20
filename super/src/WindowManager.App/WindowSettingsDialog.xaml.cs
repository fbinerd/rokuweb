using System.Windows;
using WindowManager.App.ViewModels;

namespace WindowManager.App;

public partial class WindowSettingsDialog : Window
{
    public WindowSettingsDialog(WindowConfigurationViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ViewModel = viewModel;
    }

    public WindowConfigurationViewModel ViewModel { get; }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
