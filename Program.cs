using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PcMonitor
{
    internal static class Program
    {
        [DllImport("user32.dll")] static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

        [STAThread]
        static void Main()
        {
            using var mutex = new Mutex(true, "Global\\PcMinimalMonitor_SantaNyb_SingleInstance", out bool isNew);
            if (!isNew)
            {
                // Если копия уже запущена — находим её окно по части заголовка и выводим на экран
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("PcMonitor"))
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                        SetForegroundWindow(p.MainWindowHandle);
                        break;
                    }
                }
                return;
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
        }
    }
}
