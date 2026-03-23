using System;
using System.Threading.Tasks;
using System.Windows;
using WindowManager.App.ViewModels;

namespace WindowManager.App;

public partial class TvProfileSetupDialog : Window
{
    private bool _hasRefreshedOnOpen;

    public TvProfileSetupDialog(TvProfileSetupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ViewModel = viewModel;
    }

    public TvProfileSetupViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hasRefreshedOnOpen)
        {
            return;
        }

        _hasRefreshedOnOpen = true;
        await Task.Yield();
        await RefreshTargetsAsync();
    }

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

    private void OnAssociateTargetClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.AssociateSelectedTarget())
        {
            MessageBox.Show(
                this,
                "Nao foi possivel associar a TV. Verifique se existe uma TV do perfil e uma TV detectada selecionadas com MAC identificavel.",
                "Associacao nao realizada",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void OnRefreshTargetsClick(object sender, RoutedEventArgs e)
    {
        await RefreshTargetsAsync();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private async Task RefreshTargetsAsync()
    {
        try
        {
            await ViewModel.RefreshAvailableTargetsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Falha ao redescobrir TVs",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
