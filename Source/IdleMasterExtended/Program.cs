using System;
using System.Windows.Forms;
namespace IdleMasterExtended
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            if (!Environment.Is64BitOperatingSystem) { MessageBox.Show("Idle Master Extended requires 64-bit Windows 10 or Windows 11."); return; }
            Application.ThreadException += (o, a) => Logger.Exception(a.Exception, "Application UI");
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool created;
            using (var instance = new System.Threading.Mutex(true, @"Local\IdleMasterExtended.Moriko1", out created))
            {
                if (!created) { MessageBox.Show("Idle Master Extended is already open. Check the taskbar or system tray."); return; }
                try { Application.Run(new frmMain()); }
                finally { instance.ReleaseMutex(); }
            }
        }
    }
}
