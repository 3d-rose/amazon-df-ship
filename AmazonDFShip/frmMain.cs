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
            m_Logger.Info("Application version {0} starting...", m_strVersion);
        }

        // -----------------------------------------------------------------------
        // Form load
        // -----------------------------------------------------------------------

        private void frmMain_Load(object sender, EventArgs e)
        {
            if (Program.IsHeadless)
                LoadHeadless();
            else
                LoadInteractive();
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
                Application.Exit();
                return;
            }

            if (!InitialiseTokens())
            {
                Application.Exit();
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

            // Authenticate against the database without showing a dialog.
            // frmDatabaseLogin exposes a TryAutoLogin() that reads the same
            // config keys and calls Database.Instance.Connect internally.
            string dbUser = ConfigurationManager.AppSettings["DB.Username"] ?? string.Empty;
            string dbPass = ConfigurationManager.AppSettings["DB.Password"] ?? string.Empty;

            var login = new frmDatabaseLogin(this);
            if (!login.TryAutoLogin(dbUser, dbPass))
            {
                m_Logger.Fatal("Headless DB login failed — check DB.Username / DB.Password in App.config.");
                Application.Exit();
                return;
            }

            if (!InitialiseTokens())
            {
                Application.Exit();
                return;
            }

            OrderManager.Instance.RetrieveOrders();

            if (OrderManager.Instance.Orders.Count == 0)
            {
                m_Logger.Info("Headless run: no orders found — exiting.");
                Application.Exit();
                return;
            }

            // ProcessAllOrders launches its own background thread, so this
            // returns immediately.  OnShippingLabelsFinished() will call
            // Application.Exit() when the run is complete.
            OrderManager.Instance.ProcessAllOrders();
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

        public void AddToLogTextBox(string msg)
        {
            string toAppend = $"[{DateTime.Now:MM/dd/yyyy}|{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}";
            Invoke((MethodInvoker)(() =>
            {
                txtLog.AppendText(toAppend);
                txtLog.ScrollToCaret();
            }));
        }

        public void UpdateProcessLabel(string msg) =>
            Invoke((MethodInvoker)(() => lblProcessing.Text = msg));

        public void UpdateSuccessFailure(int success, int failure)
        {
            Invoke((MethodInvoker)(() => lblSuccess.Text = "Success: " + success));
            Invoke((MethodInvoker)(() => lblFailures.Text = "Failure: " + failure));
        }

        public void UpdateOrderCount(int orders3drox, int orders3drpb)
        {
            Invoke((MethodInvoker)(() => lblOrderCount11.Text = "Orders for 3DROX: " + orders3drox));
            Invoke((MethodInvoker)(() => lblOrderCount20.Text = "Orders for 3DRPB: " + orders3drpb));
        }

        public void OnShippingLabelsFinished()
        {
            // In headless mode there is nothing to update — just exit.
            if (Program.IsHeadless)
            {
                Application.Exit();
                return;
            }

            Invoke((MethodInvoker)(() => btnGenerateShippingLabels.Enabled = true));
            Invoke((MethodInvoker)(() => btnRefresh.Enabled = true));

            AddToLogTextBox("Automatically refreshing batch list...");
            RefreshBatchList();
        }

        // -----------------------------------------------------------------------
        // Batch list helpers
        // -----------------------------------------------------------------------

        public void PopulateBatchList()
        {
            if (OrderManager.Instance.Batches.Count == 0)
            {
                Invoke((MethodInvoker)(() => btnGenerateShippingLabels.Enabled = false));
                return;
            }

            foreach (string s in OrderManager.Instance.Batches)
            {
                Invoke((MethodInvoker)(() =>
                {
                    int idx = clbBatches.Items.Add(s);
                    clbBatches.SetItemChecked(idx, true);
                }));
            }

            bool hasBatches = clbBatches.Items.Count > 0;
            Invoke((MethodInvoker)(() => btnGenerateShippingLabels.Enabled = hasBatches));
        }

        private void RefreshBatchList()
        {
            Invoke((MethodInvoker)(() => clbBatches.Items.Clear()));
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
            Environment.Exit(0);
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