using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace AmazonDFShip
{
    static class Program
    {
        // -----------------------------------------------------------------------
        // Public flags — read by frmMain and OrderManager
        // -----------------------------------------------------------------------

        /// <summary>
        /// True when the process was launched with the <c>--auto</c> argument.
        /// In this mode frmMain stays hidden and triggers processing automatically,
        /// then calls <see cref="Application.Exit"/> when the run finishes.
        /// </summary>
        public static bool IsHeadless { get; private set; }

        // -----------------------------------------------------------------------
        // Entry point
        // -----------------------------------------------------------------------

        /// <summary>Main entry point for the application.</summary>
        [STAThread]
        static void Main(string[] args)
        {
            // Recognised switch: --auto  (case-insensitive, leading dashes optional)
            IsHeadless = args.Any(a =>
                a.TrimStart('-', '/').Equals("auto", StringComparison.OrdinalIgnoreCase));

            var mutex = new Mutex(false, "AMAZONDSSLGMUTEX");
            try
            {
                if (mutex.WaitOne(0, false))
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new frmMain());
                }
                else
                {
                    MessageBox.Show(
                        "Another instance of the application is already running.",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                mutex?.Close();
            }
        }
    }
}