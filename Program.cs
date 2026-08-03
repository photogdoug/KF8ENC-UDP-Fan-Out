using System.Windows.Forms;

namespace WsjtxUdpFanout;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Any(arg => arg is "--help" or "-h"))
        {
            MessageBox.Show(
                "WSJT-X UDP Fanout\n\n" +
                "Options:\n" +
                "  --listen IPv4:port\n" +
                "  --target name=IPv4:port\n" +
                "  --bidirectional\n" +
                "  --read-only\n" +
                "  --config path",
                "Command-line options",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            using var relay = new RelayService(args);
            Application.Run(new MainForm(relay));
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "WSJT-X UDP Fanout", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"WSJT-X UDP Fanout could not start.\n\n{ex.Message}",
                "WSJT-X UDP Fanout",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
