
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SuperLauncher
{
    public partial class MainWindow : Window
    {
        private string _selectedChannel = "stable";
        private readonly string _toolsPath;
        private readonly string _ffmpegExe;
        private readonly string _ytDlpExe;
        private readonly string _dotnetExe;

        public MainWindow()
        {
            InitializeComponent();
            _toolsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "super", "src", "WindowManager.App", "bin", "Release", "net481", "tools");
            _ffmpegExe = Path.Combine(_toolsPath, "ffmpeg", "ffmpeg.exe");
            _ytDlpExe = Path.Combine(_toolsPath, "yt-dlp.exe");
            _dotnetExe = "dotnet"; // Assume in PATH
            btnCheckTools.Click += BtnCheckTools_Click;
            btnSelectChannel.Click += BtnSelectChannel_Click;
            btnCheckUpdate.Click += BtnCheckUpdate_Click;
            btnBackupRestore.Click += BtnBackupRestore_Click;
            btnLaunchApp.Click += BtnLaunchApp_Click;
        }

        private void Log(string msg)
        {
            txtLogs.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
            txtLogs.ScrollToEnd();
        }

        private async void BtnCheckTools_Click(object sender, RoutedEventArgs e)
        {
            Log("Verificando ferramentas...");
            bool ok = true;
            if (!File.Exists(_ffmpegExe))
            {
                Log("ffmpeg.exe não encontrado!");
                ok = false;
            }
            else
            {
                Log("ffmpeg.exe OK");
            }
            if (!File.Exists(_ytDlpExe))
            {
                Log("yt-dlp.exe não encontrado!");
                ok = false;
            }
            else
            {
                Log("yt-dlp.exe OK");
            }
            if (!CheckDotnet())
            {
                Log(".NET não encontrado no PATH!");
                ok = false;
            }
            else
            {
                Log(".NET OK");
            }
            if (ok)
                Log("Todas as ferramentas estão presentes.");
            else
                Log("Alguma ferramenta está faltando. Instale manualmente se necessário.");
        }

        private bool CheckDotnet()
        {
            try
            {
                var psi = new ProcessStartInfo(_dotnetExe, "--version")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc.WaitForExit(2000);
                return proc.ExitCode == 0;
            }
            catch { return false; }
        }

        private void BtnSelectChannel_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Window { Title = "Selecionar Canal", Width = 250, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this };
            var panel = new StackPanel { Margin = new Thickness(10) };
            var combo = new ComboBox { ItemsSource = new[] { "stable", "develop", "local" }, SelectedValue = _selectedChannel };
            panel.Children.Add(new TextBlock { Text = "Escolha o canal de atualização:", Margin = new Thickness(0,0,0,8) });
            panel.Children.Add(combo);
            var btn = new Button { Content = "OK", Margin = new Thickness(0,10,0,0), IsDefault = true };
            btn.Click += (_,__) => { _selectedChannel = combo.SelectedValue?.ToString() ?? "stable"; dlg.DialogResult = true; dlg.Close(); };
            panel.Children.Add(btn);
            dlg.Content = panel;
            dlg.ShowDialog();
            Log($"Canal selecionado: {_selectedChannel}");
        }

        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            // Sempre pede canal antes do update
            BtnSelectChannel_Click(null, null);
            Log($"--- INICIANDO BUSCA DE UPDATE ---");
            Log($"Canal selecionado: {_selectedChannel}");
            string url = $"https://fbinerd.github.io/rokuweb/updates/{_selectedChannel}/latest-super.json";
            try
            {
                Log($"Buscando manifesto em: {url}");
                using var http = new HttpClient();
                var resp = await http.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    Log($"Falha ao consultar update: {resp.StatusCode}");
                    return;
                }
                var json = await resp.Content.ReadAsStringAsync();
                Log($"Manifesto recebido: {json.Substring(0, Math.Min(120, json.Length))}...");
                dynamic manifest = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(json);
                string latestVersion = manifest?["currentVersion"]?.ToString() ?? "";
                string currentVersion = GetCurrentVersion();
                Log($"Versão atual: {currentVersion} | Última do canal: {latestVersion}");
                if (string.IsNullOrWhiteSpace(latestVersion) || string.IsNullOrWhiteSpace(currentVersion))
                {
                    Log("Não foi possível determinar as versões.");
                    return;
                }
                if (latestVersion == currentVersion)
                {
                    Log($"Já está na versão mais recente: {currentVersion}");
                    return;
                }
                Log($"Update disponível: {latestVersion} (atual: {currentVersion})");
                // Backup automático
                Log("Iniciando backup automático...");
                string backupPath = await BackupDataAsync(latestVersion);
                // Baixar pacote
                string pkgUrl = manifest?["releases"]?[0]?["fullPackageUrl"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(pkgUrl))
                {
                    Log("URL do pacote não encontrada no manifesto.");
                    return;
                }
                string tempDir = Path.Combine(Path.GetTempPath(), "super-update-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                string zipPath = Path.Combine(tempDir, "update.zip");
                Log($"Baixando pacote: {pkgUrl}");
                using (var pkgResp = await http.GetAsync(pkgUrl))
                using (var fs = File.Create(zipPath))
                {
                    await pkgResp.Content.CopyToAsync(fs);
                }
                Log($"Download concluído: {zipPath}");
                // Extrair pacote
                string appRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "super", "src", "WindowManager.App", "bin", "Release", "net481");
                try
                {
                    Log("Aplicando update...");
                    System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, appRoot, true);
                    Log("Update aplicado com sucesso!");
                }
                catch (Exception ex)
                {
                    Log($"ERRO ao aplicar update: {ex.Message}");
                    Log("Restaurando backup...");
                    await RestoreDataAsync(backupPath);
                    Log("Backup restaurado.");
                }
                Log($"--- FIM DO PROCESSO DE UPDATE ---");
            }
            catch (Exception ex)
            {
                Log($"Erro ao checar/aplicar update: {ex.Message}");
            }
        }

        private string GetCurrentVersion()
        {
            // Lê a versão do arquivo manifest
            try
            {
                string manifestPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "manifest");
                if (!File.Exists(manifestPath)) return "";
                foreach (var line in File.ReadAllLines(manifestPath))
                {
                    if (line.StartsWith("major_version"))
                        return line.Split('=')[1].Trim() + "." +
                               GetManifestValue(manifestPath, "minor_version") + "." +
                               GetManifestValue(manifestPath, "build_version");
                }
                return "";
            }
            catch { return ""; }
        }

        private string GetManifestValue(string path, string key)
        {
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.StartsWith(key))
                    return line.Split('=')[1].Trim();
            }
            return "";
        }

        private async Task<string> BackupDataAsync(string releaseId)
        {
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string dataRoot = Path.Combine(localAppData, "WindowManagerBroadcast");
                if (!Directory.Exists(dataRoot)) return "";
                string backupRoot = Path.Combine(localAppData, "WindowManagerBroadcast", "backups");
                Directory.CreateDirectory(backupRoot);
                string backupName = $"pre-update-{DateTime.Now:yyyyMMdd-HHmmss}-{releaseId}.zip";
                string backupPath = Path.Combine(backupRoot, backupName);
                if (File.Exists(backupPath)) File.Delete(backupPath);
                System.IO.Compression.ZipFile.CreateFromDirectory(dataRoot, backupPath);
                Log($"Backup criado em: {backupPath}");
                return backupPath;
            }
            catch (Exception ex)
            {
                Log($"Erro ao criar backup: {ex.Message}");
                return "";
            }
        }

        private async Task RestoreDataAsync(string backupZipPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(backupZipPath) || !File.Exists(backupZipPath)) return;
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string dataRoot = Path.Combine(localAppData, "WindowManagerBroadcast");
                if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
                System.IO.Compression.ZipFile.ExtractToDirectory(backupZipPath, dataRoot);
                Log($"Backup restaurado em: {dataRoot}");
            }
            catch (Exception ex)
            {
                Log($"Erro ao restaurar backup: {ex.Message}");
            }
        }
        }

        private void BtnBackupRestore_Click(object sender, RoutedEventArgs e)
        {
            Log("Backup manual de dados...");
            Task.Run(async () =>
            {
                string backupPath = await BackupDataAsync("manual");
                Log($"Backup manual salvo em: {backupPath}");
            });
        }

        private void BtnLaunchApp_Click(object sender, RoutedEventArgs e)
        {
            string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "super", "src", "WindowManager.App", "bin", "Release", "net481", "SuperPainel.exe");
            if (!File.Exists(exePath))
            {
                Log($"SuperPainel.exe não encontrado em {exePath}");
                return;
            }
            Log("Iniciando SuperPainel...");
            try
            {
                Process.Start(new ProcessStartInfo(exePath) { WorkingDirectory = Path.GetDirectoryName(exePath) });
            }
            catch (Exception ex)
            {
                Log($"Erro ao iniciar SuperPainel: {ex.Message}");
            }
        }
    }
}
