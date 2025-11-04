/*
 File: Program.cs
 Responsibility:
   - Application entry point for the WinForms app.
   - Initializes application configuration and launches MainForm.
*/

using System;
using System.Windows.Forms;

namespace CoProxyApp
{
    /// <summary>
    /// Application entry point.
    /// </summary>
    static class Program
    {
        /// <summary>
        /// Main method that configures and starts the WinForms message loop.
        /// </summary>
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize(); // Optional in .NET 6+
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());  // Main UI form
        }
    }
}
