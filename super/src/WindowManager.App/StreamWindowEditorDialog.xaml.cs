using System.Windows;
using WindowManager.App.ViewModels;

namespace WindowManager.App;

public partial class StreamWindowEditorDialog : Window
{
    public StreamWindowEditorDialog(StreamWindowEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ViewModel = viewModel;
    }

    public StreamWindowEditorViewModel ViewModel { get; }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
