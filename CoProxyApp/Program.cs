using System;
using System.Windows.Forms;

namespace CoProxyApp
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize(); // Optional in .NET 6+
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());  // Your main form (replacing MainWindow)
        }
    }
}
