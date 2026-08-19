using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NLog;

namespace AmazonDFShip
{
    // =========================================================================
    // Order
    // =========================================================================

    class Order
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public int Invoice { get; }
        public string OrderId { get; }
        public int SiteId { get; }
        public string ShippingMethod { get; }
        public string BatchNbr { get; }
        public ShippingLabel ShippingLabel { get; private set; }

        public bool IsCancelled => ShippingLabel?.IsCancelled ?? false;

        public Order(int invoice, string orderId, int siteId,
                     string shipMethod, string batch)
        {
            Invoice = invoice;
            OrderId = orderId;
            SiteId = siteId;
            ShippingMethod = shipMethod;
            BatchNbr = batch;
        }

        public bool InitiateShippingLabel()
        {
            AccessInfo info;
            switch (SiteId)
            {
                case 11: info = OrderManager.Instance.AccessInfo3DROX; break;
                case 20: info = OrderManager.Instance.AccessInfo3DRPB; break;
                default:
                    Logger.Error(
                        "Invalid site id ({0}) for invoice {1}. Cannot create shipping label.",
                        SiteId, Invoice);
                    return false;
            }

            ShippingLabel = new ShippingLabel(info, Invoice, OrderId, ShippingMethod);
            OrderManager.Instance.AddToLogTextBox(
                $"Working on order: {OrderId}  invoice: {Invoice}  ship method: {ShippingMethod}");
            return true;
        }

        public bool ProcessShippingLabel()
        {
            if (!ShippingLabel.RunLabelOperations())
            {
                if (IsCancelled)
                    Logger.Warn("Invoice {0} is cancelled — will be stamped in database.", Invoice);
                else
                    Logger.Error("Label operations failed for invoice {0}.", Invoice);
                return false;
            }

            Logger.Info("Saving ZPL for invoice {0}.", Invoice);
            if (!SaveShippingLabel())
            {
                Logger.Error("Failed to save ZPL for invoice {0}.", Invoice);
                return false;
            }

            if (!UpdateTracking())
            {
                Logger.Error("Failed to update tracking for invoice {0} (tracking: {1}).",
                    Invoice, ShippingLabel.Tracking);
                return false;
            }

            Database.Instance.LogShipData(this);
            return true;
        }

        private bool SaveShippingLabel()
        {
            string primary = $"{OrderManager.Instance.LabelPath}\\{Invoice}.zpl";
            string temp = $"temp\\{Invoice}.zpl";
            try
            {
                OrderManager.Instance.AddToLogTextBox($"Saving ZPL for invoice: {Invoice}");
                File.WriteAllText(primary, ShippingLabel.ZPL);
                File.WriteAllText(temp, ShippingLabel.ZPL);
                return true;
            }
            catch (IOException ex)
            {
                Logger.Error(ex, "IO error saving ZPL for invoice {0}.", Invoice);
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Unexpected error saving ZPL for invoice {0}.", Invoice);
                return false;
            }
        }

        private bool UpdateTracking()
        {
            OrderManager.Instance.AddToLogTextBox($"Updating tracking for invoice: {Invoice}");
            return Database.Instance.UpdateTrackingForInvoice(
                Invoice, ShippingLabel.Tracking, ShippingLabel.GetCarrierShortName());
        }
    }

    // =========================================================================
    // OrderManager
    // =========================================================================

    class OrderManager
    {
        // -----------------------------------------------------------------------
        // Singleton
        // -----------------------------------------------------------------------

        private static OrderManager m_Instance;
        private static readonly object s_Padlock = new object();

        public static OrderManager Instance
        {
            get
            {
                lock (s_Padlock)
                    return m_Instance ?? (m_Instance = new OrderManager());
            }
        }

        // -----------------------------------------------------------------------
        // Fields
        // -----------------------------------------------------------------------

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private frmMain m_Form;
        private AccessInfo m_AccessInfo3DROX;
        private AccessInfo m_AccessInfo3DRPB;

        private string m_strLabelPath;
        public string LabelPath => m_strLabelPath;
        public AccessInfo AccessInfo3DROX => m_AccessInfo3DROX;
        public AccessInfo AccessInfo3DRPB => m_AccessInfo3DRPB;

        // Fully qualified to avoid ambiguity with System.Windows.Forms.Timer
        private System.Threading.Timer m_TokenTimer;
        private const int TokenCheckIntervalMs = 60_000;
        private const int TokenMaxAgeMinutes = 55;

        private volatile bool m_bTerminating;
        private volatile bool m_bGenerating;
        public bool Generating => m_bGenerating;

        // Not readonly — passed as ref to Database.GetOrders
        private List<Order> m_lstOrders = new List<Order>();
        private List<string> m_lstBatches = new List<string>();
        private List<Order> m_lstOrders3drox;
        private List<Order> m_lstOrders3drpb;
        private List<Order> m_lstSuccess;
        private List<Order> m_lstFailure;
        private List<Order> m_lstCancelled;

        public List<Order> Orders => m_lstOrders;
        public List<string> Batches => m_lstBatches;

        // -----------------------------------------------------------------------
        // Construction
        // -----------------------------------------------------------------------

        private OrderManager() { }

        // -----------------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------------

        public bool Initialize(frmMain form)
        {
            m_Form = form;

            m_AccessInfo3DROX = AccessInfo.Create(eCenter.C_3DROX_ADWL);
            m_AccessInfo3DRPB = AccessInfo.Create(eCenter.C_3DRPB_AOTW);

            if (m_AccessInfo3DROX == null || m_AccessInfo3DRPB == null)
            {
                string which = (m_AccessInfo3DROX == null && m_AccessInfo3DRPB == null)
                    ? "3DROX and 3DRPB accounts"
                    : m_AccessInfo3DROX == null ? "3DROX account" : "3DRPB account";

                MessageBox.Show(
                    $"Could not obtain a valid access token for the {which}.{Environment.NewLine}" +
                    "Please inform IT of this error.",
                    "Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

                m_bTerminating = true;
                return false;
            }

            m_strLabelPath = Database.Instance.GetLabelPath();

            m_TokenTimer = new System.Threading.Timer(
                OnTokenTimerElapsed, null, TokenCheckIntervalMs, TokenCheckIntervalMs);

            return true;
        }

        public void TerminateStart()
        {
            m_bTerminating = true;
            m_TokenTimer?.Dispose();
            m_TokenTimer = null;
        }

        // -----------------------------------------------------------------------
        // Token refresh
        // -----------------------------------------------------------------------

        private void OnTokenTimerElapsed(object state)
        {
            if (m_bTerminating)
            {
                m_TokenTimer?.Dispose();
                return;
            }

            double roxAge = (DateTime.Now - m_AccessInfo3DROX.TokenRetrievalTime).TotalMinutes;
            double rpbAge = (DateTime.Now - m_AccessInfo3DRPB.TokenRetrievalTime).TotalMinutes;

            if (roxAge > TokenMaxAgeMinutes || rpbAge > TokenMaxAgeMinutes)
            {
                AddToLogTextBox("Refreshing tokens...");
                RefreshTokens();
            }
        }

        public void RefreshTokens()
        {
            if (m_bTerminating) return;

            // & (non-short-circuit) ensures both centers are attempted even if the first fails
            bool ok = m_AccessInfo3DROX.Refresh() & m_AccessInfo3DRPB.Refresh();

            if (ok)
            {
                AddToLogTextBox("Tokens refreshed successfully.");
                Logger.Info("Access tokens refreshed successfully.");
            }
            else
            {
                Logger.Fatal("Token refresh failed. Check preceding log entries.");
            }
        }

        // -----------------------------------------------------------------------
        // Order retrieval
        // -----------------------------------------------------------------------

        public void RetrieveOrders()
        {
            m_lstOrders.Clear();
            m_lstBatches.Clear();
            m_lstOrders3drox = null;
            m_lstOrders3drpb = null;

            List<int> exclusions = LoadInvoiceExclusions();

            int count = Database.Instance.GetOrders(
                ref m_lstOrders, exclusions.Count > 0 ? exclusions : null);

            if (count == 0)
            {
                AddToLogTextBox("No Amazon DS orders found to generate shipping labels for.");
                Logger.Info("No Amazon DS orders found.");
                return;
            }

            m_lstOrders3drox = m_lstOrders.Where(o => o.SiteId == 11).ToList();
            m_lstOrders3drpb = m_lstOrders.Where(o => o.SiteId == 20).ToList();

            m_Form.UpdateOrderCount(m_lstOrders3drox.Count, m_lstOrders3drpb.Count);

            foreach (Order o in m_lstOrders)
            {
                if (!m_lstBatches.Contains(o.BatchNbr))
                    m_lstBatches.Add(o.BatchNbr);
            }
            m_lstBatches.Sort();

            AddToLogTextBox($"Found {count} Amazon DS order(s) to generate shipping labels for.");
            Logger.Info("Found {0} Amazon DS order(s).", count);
        }

        private List<int> LoadInvoiceExclusions()
        {
            const string ExclusionFile = "invoice_exclusions.txt";
            var list = new List<int>();

            if (!File.Exists(ExclusionFile)) return list;

            string[] lines = File.ReadAllLines(ExclusionFile);
            Logger.Info("Read {0} line(s) from {1}.", lines.Length, ExclusionFile);

            foreach (string line in lines)
            {
                if (!int.TryParse(line, out int invoice))
                {
                    Logger.Warn("Could not parse '{0}' as integer in exclusion file. Skipping.", line);
                    continue;
                }
                if (list.Contains(invoice))
                {
                    Logger.Warn("Duplicate exclusion {0} ignored.", invoice);
                    continue;
                }
                list.Add(invoice);
            }
            return list;
        }

        // -----------------------------------------------------------------------
        // Label processing — public entry points
        // -----------------------------------------------------------------------

        /// <summary>Starts label generation for all loaded orders on a background thread.</summary>
        public void ProcessAllOrders()
        {
            if (!EnsureOrdersReady()) return;
            Task.Run(() => RunProcessingLoop(m_lstOrders, "all batches"));
        }

        /// <summary>Starts label generation for orders belonging to the given batches.</summary>
        public void ProcessOrdersForBatches(List<string> batches)
        {
            if (!EnsureOrdersReady()) return;

            if (batches == null || batches.Count == 0)
            {
                Logger.Error("ProcessOrdersForBatches called with no batches specified.");
                return;
            }

            List<Order> subset = m_lstOrders
                .Where(o => batches.Contains(o.BatchNbr))
                .ToList();

            if (subset.Count == 0)
            {
                Logger.Warn("No orders matched the selected batch filter.");
                AddToLogTextBox("No orders to process based on batches selected.");
                return;
            }

            Task.Run(() => RunProcessingLoop(subset, $"{batches.Count} batch(es)"));
        }

        // -----------------------------------------------------------------------
        // Core processing loop
        // -----------------------------------------------------------------------

        private void RunProcessingLoop(List<Order> orders, string context)
        {
            m_lstSuccess = new List<Order>();
            m_lstFailure = new List<Order>();
            m_lstCancelled = new List<Order>();
            m_bGenerating = true;

            DateTime startTime = DateTime.Now;
            SlackNotifier.Post($"DF process started at `{startTime:HH:mm:ss}`");

            AddToLogTextBox($"Starting shipping label generation for {context}.");
            Logger.Info("Starting shipping label generation for {0}.", context);

            int done = 0;

            foreach (Order order in orders)
            {
                if (!order.InitiateShippingLabel())
                {
                    m_lstFailure.Add(order);
                    UpdateSuccessFailure();
                    continue;
                }

                Thread.Sleep(1000);

                if (order.ProcessShippingLabel())
                {
                    m_lstSuccess.Add(order);
                    AddToLogTextBox(
                        $"Shipping label generated for invoice {order.Invoice} \u2714");
                }
                else if (order.IsCancelled)
                {
                    // Confirmed cancelled — stamp the DB, don't count as a failure
                    Database.Instance.MarkOrderCancelled(order.Invoice);
                    m_lstCancelled.Add(order);
                    AddToLogTextBox(
                        $"Invoice {order.Invoice} is cancelled — marked in database \u26D4");
                    Logger.Info("Invoice {0} stamped as cancelled.", order.Invoice);
                }
                else
                {
                    m_lstFailure.Add(order);
                    AddToLogTextBox(
                        $"Failed to generate shipping label for invoice {order.Invoice} \u274C");
                }

                ++done;
                UpdateProcessingLabel($"Generated label: {done}/{orders.Count}");
                UpdateSuccessFailure();
            }

            TimeSpan elapsed = DateTime.Now - startTime;

            UpdateProcessingLabel($"Done. Generated {m_lstSuccess.Count} shipping label(s).");
            AddToLogTextBox("Requested operation has been completed.");
            AddToLogTextBox($"Shipping labels generated: {m_lstSuccess.Count} \u2714");
            AddToLogTextBox($"Cancelled orders skipped:  {m_lstCancelled.Count} \u26D4");
            AddToLogTextBox($"Shipping labels failed:    {m_lstFailure.Count} \u274C");

            Logger.Info(
                "Label generation finished. Success: {0}  Cancelled: {1}  Failure: {2}",
                m_lstSuccess.Count, m_lstCancelled.Count, m_lstFailure.Count);

            SlackNotifier.Post(BuildFinishMessage(elapsed));

            m_bGenerating = false;
            m_Form.OnShippingLabelsFinished();
        }

        /// <summary>
        /// Builds the Slack finish message.  Kept separate from the loop so the
        /// wording can be adjusted without touching processing logic.
        /// </summary>
        private string BuildFinishMessage(TimeSpan elapsed)
        {
            int totalMinutes = (int)elapsed.TotalMinutes;
            int seconds = elapsed.Seconds;

            var sb = new StringBuilder();

            sb.AppendLine(
                $"DF process finished — took `{totalMinutes}m {seconds}s`");

            sb.AppendLine(
                $"Processed: {m_lstSuccess.Count} \u2714  |  " +
                $"Skipped (cancelled): {m_lstCancelled.Count} \u26D4  |  " +
                $"Failed: {m_lstFailure.Count} \u274C");

            if (m_lstCancelled.Count > 0)
            {
                string ids = string.Join(", ", m_lstCancelled.Select(o => o.OrderId));
                sb.AppendLine($"Skipped order IDs: `{ids}`");
            }

            if (m_lstFailure.Count > 0)
            {
                string ids = string.Join(", ", m_lstFailure.Select(o => o.OrderId));
                sb.AppendLine($"Failed order IDs: `{ids}`");
            }

            return sb.ToString().TrimEnd();
        }

        private bool EnsureOrdersReady()
        {
            if (m_lstOrders.Count > 0) return true;
            Logger.Error("Processing requested but no orders are loaded.");
            return false;
        }

        // -----------------------------------------------------------------------
        // Form bridge helpers
        // -----------------------------------------------------------------------

        public void AddToLogTextBox(string msg) => m_Form.AddToLogTextBox(msg);
        public void UpdateProcessingLabel(string msg) => m_Form.UpdateProcessLabel(msg);

        public void UpdateSuccessFailure()
        {
            m_Form.UpdateSuccessFailure(
                m_lstSuccess?.Count ?? 0,
                m_lstFailure?.Count ?? 0);
        }
    }
}