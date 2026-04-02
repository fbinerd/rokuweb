using System.Windows;
using System.Windows.Controls;

namespace WindowManager.App
{
    public partial class SplashScreen : Window
    {
        public SplashScreen()
        {
            InitializeComponent();
            SetDefaultBranchCheckBox.Checked += OnUpdateOptionChanged;
            SetDefaultBranchCheckBox.Unchecked += OnUpdateOptionChanged;
            AutoUpdateCheckBox.Checked += OnUpdateOptionChanged;
            AutoUpdateCheckBox.Unchecked += OnUpdateOptionChanged;
            SyncUpdateOptionState();
        }

        public void SetInstalledVersion(string version)
        {
            InstalledVersionText.Text = $"Versão instalada: {version}";
        }

        public void SetRemoteVersion(string version)
        {
            RemoteVersionText.Text = $"Versão disponível: {version}";
        }

        public void ShowProgressBar(bool show)
        {
            ProgressContainer.Visibility = show ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (!show)
            {
                ProgressDetailsHost.Visibility = System.Windows.Visibility.Collapsed;
                ProgressDetailsShadowText.Text = string.Empty;
                ProgressDetailsText.Text = string.Empty;
            }
        }

        public void SetStatus(string message, double progress)
        {
            StatusText.Text = message;
            ProgressBar.Value = progress;
        }

        public void SetProgressDetails(string? details)
        {
            var hasDetails = !string.IsNullOrWhiteSpace(details);
            var text = hasDetails ? details! : string.Empty;
            ProgressDetailsShadowText.Text = text;
            ProgressDetailsText.Text = text;
            ProgressDetailsHost.Visibility = hasDetails
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        }

        public bool IsAutoUpdateChecked => AutoUpdateCheckBox.IsChecked == true;
        public void SetAutoUpdateChecked(bool value)
        {
            AutoUpdateCheckBox.IsChecked = value;
            SyncUpdateOptionState();
        }
        public bool IsSetDefaultBranchChecked => SetDefaultBranchCheckBox.IsChecked == true;
        public void SetSetDefaultBranchChecked(bool value)
        {
            SetDefaultBranchCheckBox.IsChecked = value;
            SyncUpdateOptionState();
        }
        public System.Windows.Controls.ComboBox ChannelCombo => ChannelComboBox;
        public string SelectedChannel => ChannelComboBox.SelectedValue?.ToString() ?? "stable";
        public void SetChannels(string[] canais, string selected)
        {
            ChannelComboBox.ItemsSource = canais;
            ChannelComboBox.SelectedValue = selected;
        }
        public System.Windows.Controls.Button GetOkButton() => OkButton;

        private void OnUpdateOptionChanged(object sender, RoutedEventArgs e)
        {
            SyncUpdateOptionState();
        }

        private void SyncUpdateOptionState()
        {
            var canAutoUpdate = SetDefaultBranchCheckBox.IsChecked == true;
            AutoUpdateCheckBox.IsEnabled = canAutoUpdate;
            if (!canAutoUpdate)
            {
                AutoUpdateCheckBox.IsChecked = false;
            }
        }
    }
}
