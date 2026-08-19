using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NLog;

namespace AmazonDFShip
{
    public partial class frmMain : Form
    {
        private static readonly Logger m_Logger = LogManager.GetCurrentClassLogger();
        private const string m_strVersion = "1.0.0.7";

        public frmMain()
        {
            InitializeComponent();
            Program.MainForm = this;
            m_Logger.Info("Application version {0} starting (headless: {1})...",
                m_strVersion, Program.IsHeadless);
        }

        // -----------------------------------------------------------------------
        // Form load
        // -----------------------------------------------------------------------

        private void frmMain_Load(object sender, EventArgs e)
        {
            if (Program.IsHeadless)
            {
                // Load runs inside Application.Run before the message loop is pumping.
                // Do the headless work on the loop instead, so BeginInvoke-based
                // logging and shutdown behave normally.
                BeginInvoke((MethodInvoker)LoadHeadless);
            }
            else
            {
                LoadInteractive();
            }
        }

        /// <summary>
        /// Normal GUI startup: show the login dialog, then retrieve orders.
        /// </summary>
        private void LoadInteractive()
        {
            var login = new frmDatabaseLogin(this);
            login.ShowDialog(this);

            if (!login.LoginSuccessful())
            {
                m_Logger.Fatal("Login failed. See previous logs.");
                // Application.Exit() is a no-op this early (the message loop has not
                // started yet), which left an empty window on screen. Queue a Close.
                BeginInvoke((MethodInvoker)Close);
                return;
            }

            if (!InitialiseTokens())
            {
                BeginInvoke((MethodInvoker)Close);
                return;
            }

            Text = "Amazon DS - Shipping Label Generator (Logged in as: "
                 + login.LoggedUsername() + ")";

            OrderManager.Instance.RetrieveOrders();
            PopulateBatchList();
        }

        /// <summary>
        /// Headless (--auto) startup: authenticate using App.config credentials,
        /// keep the window invisible, and kick off processing automatically.
        /// Required App.config keys: DB.Username  DB.Password
        /// </summary>
        private void LoadHeadless()
        {
            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;
            this.Visible = false;

            try
            {
                // Authenticate against the database without showing a dialog.
                // Note: the values are passed through verbatim — no trimming — so
                // passwords containing @ ; = ' " or spaces reach SqlConnectionStringBuilder
                // exactly as written in App.config.
                string dbUser = ConfigurationManager.AppSettings["DB.Username"] ?? string.Empty;
                string dbPass = ConfigurationManager.AppSettings["DB.Password"] ?? string.Empty;

                var login = new frmDatabaseLogin(this);
                if (!login.TryAutoLogin(dbUser, dbPass))
                {
                    Program.Report(
                        "Headless DB login failed — check DB.Username / DB.Password in App.config. " +
                        "If the password contains XML-special characters, remember that & must be " +
                        "written as &amp; and < as &lt; inside the config file.",
                        isError: true);
                    Exit(Program.ExitLoginFailed);
                    return;
                }

                if (!InitialiseTokens())
                {
                    Exit(Program.ExitTokenFailed);
                    return;
                }

                OrderManager.Instance.RetrieveOrders();

                if (OrderManager.Instance.Orders.Count == 0)
                {
                    Program.Report("Headless run: no orders found — nothing to do.");
                    Exit(Program.ExitOk);
                    return;
                }

                Program.Report(
                    $"Headless run: generating labels for {OrderManager.Instance.Orders.Count} order(s).");

                // ProcessAllOrders launches its own background task, so this returns
                // immediately.  OnShippingLabelsFinished() ends the run.
                OrderManager.Instance.ProcessAllOrders();
            }
            catch (Exception ex)
            {
                m_Logger.Fatal(ex, "Headless startup failed.");
                Program.Report("Headless startup failed: " + ex.Message, isError: true);
                Exit(Program.ExitUnhandled);
            }
        }

        /// <summary>Sets the process exit code and shuts the headless run down.</summary>
        private void Exit(int exitCode)
        {
            Environment.ExitCode = exitCode;
            Program.ShutdownHeadless();
        }

        /// <summary>
        /// Shared token-initialisation step used by both startup paths.
        /// </summary>
        private bool InitialiseTokens()
        {
            if (OrderManager.Instance.Initialize(this))
            {
                AddToLogTextBox("Obtained access tokens successfully.");
                m_Logger.Info("Obtained access tokens successfully from Amazon.");
                return true;
            }

            m_Logger.Fatal("Failed to obtain access tokens. See previous logs.");
            return false;
        }

        // -----------------------------------------------------------------------
        // Public form-bridge methods (called from OrderManager on background threads)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Runs <paramref name="action"/> on the UI thread.
        ///
        /// Every bridge method below used to call Invoke() unconditionally. Invoke throws
        /// InvalidOperationException when the control has no window handle, and it
        /// deadlocks when the message loop is not pumping — both of which happen on the
        /// --auto path, where the form is never shown. Guarding here keeps a headless run
        /// from dying inside a status update.
        /// </summary>
        private void OnUi(MethodInvoker action)
        {
            if (IsDisposed || Disposing) return;

            try
            {
                if (!IsHandleCreated)
                {
                    // No handle to marshal to. In headless mode the controls are never
                    // seen anyway, so skipping the update is correct rather than fatal.
                    if (!Program.IsHeadless)
                        m_Logger.Warn("UI update skipped: window handle not yet created.");
                    return;
                }

                if (InvokeRequired) Invoke(action);
                else action();
            }
            catch (ObjectDisposedException) { /* form closed mid-update — nothing to do */ }
            catch (InvalidOperationException ex)
            {
                m_Logger.Warn(ex, "UI update could not be marshalled to the UI thread.");
            }
        }

        public void AddToLogTextBox(string msg)
        {
            string toAppend = $"[{DateTime.Now:MM/dd/yyyy}|{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}";

            // In headless mode the textbox is invisible, so the console is the only
            // place a CLI caller can see progress.
            if (Program.IsHeadless)
            {
                Program.Report(msg);
                return;
            }

            OnUi(() =>
            {
                txtLog.AppendText(toAppend);
                txtLog.ScrollToCaret();
            });
        }

        public void UpdateProcessLabel(string msg)
        {
            if (Program.IsHeadless)
            {
                m_Logger.Info(msg);
                return;
            }

            OnUi(() => lblProcessing.Text = msg);
        }

        public void UpdateSuccessFailure(int success, int failure)
        {
            if (Program.IsHeadless) return;

            OnUi(() =>
            {
                lblSuccess.Text = "Success: " + success;
                lblFailures.Text = "Failure: " + failure;
            });
        }

        public void UpdateOrderCount(int orders3drox, int orders3drpb)
        {
            if (Program.IsHeadless)
            {
                Program.Report($"Orders for 3DROX: {orders3drox} | Orders for 3DRPB: {orders3drpb}");
                return;
            }

            OnUi(() =>
            {
                lblOrderCount11.Text = "Orders for 3DROX: " + orders3drox;
                lblOrderCount20.Text = "Orders for 3DRPB: " + orders3drpb;
            });
        }

        public void OnShippingLabelsFinished()
        {
            if (Program.IsHeadless)
            {
                // Called from the processing task, not the UI thread. Application.Exit()
                // from a background thread is unreliable, so route through ShutdownHeadless.
                Exit(OrderManager.Instance.FailureCount > 0
                    ? Program.ExitLabelFailures
                    : Program.ExitOk);
                return;
            }

            OnUi(() =>
            {
                btnGenerateShippingLabels.Enabled = true;
                btnRefresh.Enabled = true;
            });

            AddToLogTextBox("Automatically refreshing batch list...");
            RefreshBatchList();
        }

        // -----------------------------------------------------------------------
        // Batch list helpers
        // -----------------------------------------------------------------------

        public void PopulateBatchList()
        {
            if (Program.IsHeadless) return;

            if (OrderManager.Instance.Batches.Count == 0)
            {
                OnUi(() => btnGenerateShippingLabels.Enabled = false);
                return;
            }

            foreach (string s in OrderManager.Instance.Batches)
            {
                string batch = s; // capture per iteration
                OnUi(() =>
                {
                    int idx = clbBatches.Items.Add(batch);
                    clbBatches.SetItemChecked(idx, true);
                });
            }

            OnUi(() => btnGenerateShippingLabels.Enabled = clbBatches.Items.Count > 0);
        }

        private void RefreshBatchList()
        {
            if (Program.IsHeadless) return;

            OnUi(() => clbBatches.Items.Clear());
            OrderManager.Instance.RetrieveOrders();
            PopulateBatchList();
        }

        // -----------------------------------------------------------------------
        // Event handlers
        // -----------------------------------------------------------------------

        private void btnGenerateShippingLabels_Click(object sender, EventArgs e)
        {
            if (clbBatches.CheckedItems.Count == clbBatches.Items.Count)
            {
                new Thread(() => OrderManager.Instance.ProcessAllOrders()).Start();
            }
            else
            {
                var batches = clbBatches.CheckedItems.Cast<string>().ToList();
                new Thread(() => OrderManager.Instance.ProcessOrdersForBatches(batches)).Start();
            }

            btnGenerateShippingLabels.Enabled = false;
            btnRefresh.Enabled = false;
        }

        private void btnRefresh_Click(object sender, EventArgs e)
        {
            if (OrderManager.Instance.Generating)
            {
                m_Logger.Warn("Refresh pressed while labels are generating — ignored.");
                return;
            }
            RefreshBatchList();
        }

        private void clbBatches_Click(object sender, EventArgs e)
        {
            if (OrderManager.Instance.Generating)
            {
                m_Logger.Warn("Batch list clicked while labels are generating — ignored.");
                return;
            }

            bool hasSelection = clbBatches.SelectedItems.Count > 0;
            btnGenerateShippingLabels.Enabled = hasSelection && clbBatches.Items.Count > 0;
        }

        private void btnExit_Click(object sender, EventArgs e)
        {
            string prompt = OrderManager.Instance.Generating
                ? "The shipping labels are currently being generated." + Environment.NewLine
                  + "Are you sure you want to exit?"
                : "Are you sure you want to exit?";

            MessageBoxIcon icon = OrderManager.Instance.Generating
                ? MessageBoxIcon.Warning
                : MessageBoxIcon.Information;

            if (MessageBox.Show(prompt, "Exit", MessageBoxButtons.YesNo, icon) == DialogResult.Yes)
                Close();
        }

        private void frmMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            m_Logger.Info("Application is closing.");
            OrderManager.Instance.TerminateStart();
            Application.DoEvents();
            Thread.Sleep(500);
        }

        private void frmMain_FormClosed(object sender, FormClosedEventArgs e)
        {
            // Preserve whatever exit code the run produced instead of forcing 0,
            // so a scheduler can tell a clean run from a failed one.
            Program.FinaliseExit();
            Environment.Exit(Environment.ExitCode);
        }

        // -----------------------------------------------------------------------
        // Misc / test
        // -----------------------------------------------------------------------

        private void AddToLogTextBox2(string msg) => txtLog.AppendText(msg);

        private void btnTest_Click(object sender, EventArgs e)
        {
            AddToLogTextBox2(@"{\rtf1\pc Hi \bthere\b0 \u2714 \u274c \u274C");
        }
    }
}