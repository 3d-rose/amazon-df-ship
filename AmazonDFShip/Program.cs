using System;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using NLog;

namespace AmazonDFShip
{
    static class Program
    {
        private static readonly Logger m_Logger = LogManager.GetCurrentClassLogger();

        // -----------------------------------------------------------------------
        // Public flags — read by frmMain and OrderManager
        // -----------------------------------------------------------------------

        /// <summary>
        /// True when the process was launched with the <c>--auto</c> argument.
        /// In this mode frmMain stays hidden and triggers processing automatically,
        /// then shuts the message loop down when the run finishes.
        /// </summary>
        public static bool IsHeadless { get; private set; }

        /// <summary>
        /// The single frmMain instance. Held explicitly rather than looked up through
        /// Application.OpenForms, which only lists forms whose handle has been created.
        /// </summary>
        public static Form MainForm { get; internal set; }

        // -----------------------------------------------------------------------
        // Process exit codes — meaningful so a scheduler can act on them
        // -----------------------------------------------------------------------

        public const int ExitOk = 0;
        public const int ExitAlreadyRunning = 2;
        public const int ExitLoginFailed = 3;
        public const int ExitTokenFailed = 4;
        public const int ExitConfigError = 5;
        public const int ExitLabelFailures = 6;
        public const int ExitUnhandled = 10;

        // App.config keys that --auto cannot run without.
        private static readonly string[] RequiredHeadlessSettings =
        {
            "DB.Username",
            "DB.Password",
            "Amazon.ClientId",
            "AWS.AccessKeyId",
            "AWS.SecretKey",
            "AWS.Region",
            "3DROX.RefreshToken",
            "3DRPB.RefreshToken"
        };

        // -----------------------------------------------------------------------
        // Console plumbing — a WinExe has no console of its own, so attach to the
        // one that launched us.  Without this, `AmazonDFShip.exe --auto` returns
        // instantly with no output whatsoever, which is what "not working via CLI"
        // looks like from the caller's side even when the run is progressing.
        // -----------------------------------------------------------------------

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        private const int AttachParentProcess = -1;

        private static bool s_bConsoleAttached;

        /// <summary>Writes to the attached console (if any) and to the log.</summary>
        public static void Report(string message, bool isError = false)
        {
            if (s_bConsoleAttached)
            {
                if (isError) Console.Error.WriteLine(message);
                else Console.Out.WriteLine(message);
            }

            if (isError) m_Logger.Error(message);
            else m_Logger.Info(message);
        }

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

            // A CLI or Task Scheduler launch inherits the caller's working directory.
            // Every relative path in this app assumes the exe folder, so pin it here.
            try
            {
                Directory.SetCurrentDirectory(Paths.BaseDir);
            }
            catch (Exception ex)
            {
                m_Logger.Warn(ex, "Could not set working directory to {0}.", Paths.BaseDir);
            }

            if (IsHeadless)
                AttachToParentConsole();

            InstallExceptionHandlers();

            Environment.ExitCode = ExitOk;

            try
            {
                // Constructed inside the try: opening a named mutex can throw
                // UnauthorizedAccessException when the name already exists in another
                // session with incompatible rights, and that must not escape Main.
                s_Mutex = new Mutex(false, "AMAZONDSSLGMUTEX");

                bool acquired;
                try
                {
                    acquired = s_Mutex.WaitOne(0, false);
                }
                catch (AbandonedMutexException)
                {
                    // The previous run ended via Environment.Exit without releasing the
                    // mutex, so Windows flags it abandoned. WaitOne still grants ownership
                    // — it just reports the fact by throwing. Left unhandled this took the
                    // process down before any work started, which meant every --auto run
                    // after the first one failed instantly and silently.
                    m_Logger.Warn("Previous instance did not release the mutex cleanly. Continuing.");
                    acquired = true;
                }

                s_bMutexOwned = acquired;

                if (!acquired)
                {
                    const string busy = "Another instance of the application is already running.";

                    if (IsHeadless)
                    {
                        // Never pop a modal dialog in headless mode — nobody is there
                        // to dismiss it and the process would hang until killed.
                        Report(busy, isError: true);
                        Environment.ExitCode = ExitAlreadyRunning;
                    }
                    else
                    {
                        MessageBox.Show(busy, "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    return;
                }

                if (IsHeadless)
                {
                    Report($"AmazonDFShip --auto starting in {Paths.BaseDir}");

                    Paths.EnsureWorkingDirectories();

                    if (!ValidateHeadlessConfiguration())
                    {
                        Environment.ExitCode = ExitConfigError;
                        return;
                    }
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new frmMain());
            }
            catch (Exception ex)
            {
                m_Logger.Fatal(ex, "Unhandled exception in Main.");
                Report("Fatal: " + ex.Message, isError: true);
                Environment.ExitCode = ExitUnhandled;
            }
            finally
            {
                FinaliseExit();
            }
        }

        private static Mutex s_Mutex;
        private static bool s_bMutexOwned;

        /// <summary>
        /// Last thing to run before the process ends. Safe to call more than once —
        /// frmMain.FormClosed calls Environment.Exit, which pre-empts the finally block
        /// in Main, so the shutdown reporting lives here and is invoked from both.
        /// </summary>
        public static void FinaliseExit()
        {
            if (s_bFinalised) return;
            s_bFinalised = true;

            if (IsHeadless)
                Report($"AmazonDFShip --auto finished with exit code {Environment.ExitCode}.");

            LogManager.Flush(TimeSpan.FromSeconds(5));

            // Release before the process dies so the next run does not see an
            // abandoned mutex.
            try
            {
                // Only release what we actually took — releasing an unowned mutex throws.
                if (s_bMutexOwned)
                {
                    s_Mutex?.ReleaseMutex();
                    s_bMutexOwned = false;
                }
            }
            catch (ApplicationException)
            {
                // Not the owning thread — harmless, the handle is about to close anyway.
            }
            finally
            {
                s_Mutex?.Close();
                s_Mutex = null;
            }
        }

        private static bool s_bFinalised;

        // -----------------------------------------------------------------------
        // Startup helpers
        // -----------------------------------------------------------------------

        private static void AttachToParentConsole()
        {
            // Deliberately do NOT AllocConsole on failure: under Task Scheduler there
            // is no parent console and spawning one would flash a window on the desktop.
            if (!AttachConsole(AttachParentProcess))
                return;

            try
            {
                var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                Console.SetOut(stdout);
                Console.SetError(stderr);
                s_bConsoleAttached = true;
            }
            catch (IOException)
            {
                s_bConsoleAttached = false;
            }
        }

        /// <summary>
        /// Confirms every App.config key the unattended path needs is present before
        /// any network or database work starts, and reports all missing keys at once
        /// instead of failing on whichever happens to be read first.
        /// </summary>
        private static bool ValidateHeadlessConfiguration()
        {
            string[] missing;
            try
            {
                missing = RequiredHeadlessSettings
                    .Where(k => string.IsNullOrWhiteSpace(ConfigurationManager.AppSettings[k]))
                    .ToArray();
            }
            catch (ConfigurationErrorsException ex)
            {
                // Typically a malformed App.config — for example an unescaped '&' in a
                // password, which must be written as &amp; inside XML.
                Report("App.config could not be parsed: " + ex.Message, isError: true);
                m_Logger.Fatal(ex, "App.config could not be parsed.");
                return false;
            }

            if (missing.Length == 0)
                return true;

            var sb = new StringBuilder();
            sb.AppendLine("Cannot run with --auto: the following App.config <appSettings> " +
                          "keys are missing or blank:");
            foreach (string key in missing)
                sb.AppendLine("  - " + key);
            sb.AppendLine("Config file in use: " +
                (ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None).FilePath
                 ?? "(unknown)"));

            string message = sb.ToString().TrimEnd();
            Report(message, isError: true);
            m_Logger.Fatal(message);
            return false;
        }

        /// <summary>
        /// Routes unhandled exceptions to the log and to the console rather than to a
        /// modal error dialog, which in headless mode would block the process forever.
        /// </summary>
        private static void InstallExceptionHandlers()
        {
            if (IsHeadless)
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            // Only intercept in headless mode. In the GUI the default WinForms handler
            // shows the standard error dialog, and swallowing it here would leave the
            // user staring at an apparently-frozen window.
            if (IsHeadless)
            {
                Application.ThreadException += (s, e) =>
                {
                    m_Logger.Fatal(e.Exception, "Unhandled UI-thread exception.");
                    Report("Fatal (UI thread): " + e.Exception.Message, isError: true);

                    Environment.ExitCode = ExitUnhandled;
                    ShutdownHeadless();
                };
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                m_Logger.Fatal(ex, "Unhandled AppDomain exception.");
                Report("Fatal: " + (ex?.Message ?? "unknown error"), isError: true);
                Environment.ExitCode = ExitUnhandled;
                LogManager.Flush(TimeSpan.FromSeconds(5));
            };
        }

        // -----------------------------------------------------------------------
        // Shutdown
        // -----------------------------------------------------------------------

        /// <summary>
        /// Ends a headless run safely from any thread.
        ///
        /// Application.Exit() is not thread-safe and is a no-op when called before the
        /// message loop has started (which is exactly where LoadHeadless used to call
        /// it).  Marshalling onto the main form and closing it works in both cases.
        /// </summary>
        public static void ShutdownHeadless()
        {
            LogManager.Flush(TimeSpan.FromSeconds(5));

            Form main = MainForm;

            if (main == null || main.IsDisposed || !main.IsHandleCreated)
            {
                Application.Exit();
                return;
            }

            try
            {
                // BeginInvoke rather than Invoke: during Form.Load the loop is not yet
                // pumping, so a synchronous Invoke would deadlock.  BeginInvoke queues
                // the close and it runs as soon as the loop starts.
                main.BeginInvoke((MethodInvoker)(() =>
                {
                    try { main.Close(); }
                    catch (Exception ex) { m_Logger.Warn(ex, "Error closing main form."); }
                }));
            }
            catch (Exception ex)
            {
                m_Logger.Warn(ex, "Could not marshal shutdown onto the UI thread; exiting directly.");
                Application.Exit();
            }
        }
    }
}
