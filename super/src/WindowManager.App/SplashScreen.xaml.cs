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
            ProgressContainer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show)
            {
                ProgressDetailsHost.Visibility = Visibility.Collapsed;
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
            ProgressDetailsHost.Visibility = hasDetails ? Visibility.Visible : Visibility.Collapsed;
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

        public ComboBox ChannelCombo => ChannelComboBox;

        public string SelectedChannel => ChannelComboBox.SelectedValue?.ToString() ?? "stable";

        public void SetChannels(string[] channels, string selected)
        {
            ChannelComboBox.ItemsSource = channels;
            ChannelComboBox.SelectedValue = selected;
        }

        public Button GetOkButton() => OkButton;

        public void SetInteractionEnabled(bool enabled)
        {
            ChannelComboBox.IsEnabled = enabled;
            SetDefaultBranchCheckBox.IsEnabled = enabled;
            OkButton.IsEnabled = enabled;

            if (!enabled)
            {
                AutoUpdateCheckBox.IsEnabled = false;
                return;
            }

            SyncUpdateOptionState();
        }

        public void SetCompactMode(bool compact)
        {
            ChannelComboBox.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            OptionsPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            OkButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            Height = compact ? 250 : 340;
        }

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
