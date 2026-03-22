using System.Windows;
using WindowManager.App.ViewModels;

namespace WindowManager.App;

public partial class TvProfileSetupDialog : Window
{
    public TvProfileSetupDialog(TvProfileSetupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ViewModel = viewModel;
    }

    public TvProfileSetupViewModel ViewModel { get; }

    private void OnAddSelectedTargetClick(object sender, RoutedEventArgs e)
    {
        ViewModel.AddSelectedTarget();
    }

    private void OnAddManualIpClick(object sender, RoutedEventArgs e)
    {
        ViewModel.AddManualIp();
    }

    private void OnRemoveIncludedTargetClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RemoveSelectedTarget();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
