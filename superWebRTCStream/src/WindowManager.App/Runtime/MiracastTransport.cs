using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WindowManager.Core.Abstractions;
using WindowManager.Core.Models;

namespace WindowManager.App.Runtime;

public sealed class MiracastTransport : IDisplayTransport
{
    private const byte VkLwin = 0x5B;
    private const byte VkK = 0x4B;
    private const uint KeyeventfKeyup = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, int extraInfo);

    public DisplayTransportKind Kind => DisplayTransportKind.Miracast;

    public Task StartAsync(CaptureSession captureSession, DisplayTarget target, CancellationToken cancellationToken)
    {
        AppLog.Write(
            "Transporte",
            string.Format(
                "Destino Miracast selecionado: '{0}' ({1}). Abrindo painel de Cast do Windows.",
                target.Name,
                target.NetworkAddress));

        try
        {
            keybd_event(VkLwin, 0, 0, 0);
            keybd_event(VkK, 0, 0, 0);
            keybd_event(VkK, 0, KeyeventfKeyup, 0);
            keybd_event(VkLwin, 0, KeyeventfKeyup, 0);
        }
        catch
        {
        }

        AppLog.Write(
            "Transporte",
            string.Format(
                "Painel de Cast acionado para '{0}'. No Windows 11, conclua a conexao com a TV Samsung se ela aparecer na lista.",
                target.Name,
                target.NetworkAddress));
        return Task.CompletedTask;
    }

    public Task StopAsync(CaptureSession captureSession, CancellationToken cancellationToken)
    {
        AppLog.Write("Transporte", "Miracast encerrado.");
        return Task.CompletedTask;
    }
}
