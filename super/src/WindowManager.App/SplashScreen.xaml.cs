using System.Windows;

namespace WindowManager.App
{
    public partial class SplashScreen : Window
    {
        public SplashScreen()
        {
            InitializeComponent();
        }

        public void SetStatus(string message, double progress)
        {
            StatusText.Text = message;
            ProgressBar.Value = progress;
        }
    }
}
