using System.Windows;
using WindowManager.App.ViewModels;

namespace WindowManager.App;

public partial class WindowProfileSetupDialog : Window
{
    public WindowProfileSetupDialog(WindowProfileSetupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ViewModel = viewModel;
    }

    public WindowProfileSetupViewModel ViewModel { get; }

    private void OnAddWindowClick(object sender, RoutedEventArgs e)
    {
        ViewModel.AddWindow();
    }

    private void OnRemoveWindowClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RemoveSelectedWindow();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
