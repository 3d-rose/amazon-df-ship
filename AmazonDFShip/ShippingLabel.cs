using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Amazon.SellingPartnerAPIAA;
using Newtonsoft.Json;
using RestSharp;
using NLog;

namespace AmazonDFShip
{
    class ShippingLabel
    {
        // -----------------------------------------------------------------------
        // Constants
        // -----------------------------------------------------------------------

        private const int MinimumDelaySeconds = 20;
        private const int MaxPayloadRetries = 3;
        private const int SmallPayloadBytes = 2000;

        // -----------------------------------------------------------------------
        // Fields
        // -----------------------------------------------------------------------

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly AccessInfo m_Info;
        private readonly int m_iInvoice;
        private readonly string m_strOrder;
        private readonly string m_strShippingText;

        private string m_strTransactionId;
        private string m_strTransactionData;
        private string m_strTransactionStatus;
        private string m_strPayload;
        private string m_strPayloadFile;
        private string m_strZPL;
        private string m_strTracking;
        private string m_strShipMethod;
        private string m_strShipMethodName;
        private string m_strLabelContent;
        private int m_iAttempts;
        private bool m_bSavePayloads = true;
        private bool m_bSaveTransactions = true;
        private DateTime m_dtSubmissionRequest;

        // -----------------------------------------------------------------------
        // Properties
        // -----------------------------------------------------------------------

        public string TransactionId => m_strTransactionId;
        public string TransactionData => m_strTransactionData;
        public string TransactionStatus => m_strTransactionStatus;
        public string ZPL => m_strZPL;
        public string Tracking => m_strTracking;
        public string ShipMethod => m_strShipMethod;
        public string ShipMethodName => m_strShipMethodName;
        public string LabelContent => m_strLabelContent;

        /// <summary>
        /// True when the Orders API has confirmed this order is cancelled.
        /// Set during transaction-status parsing; checked by Order.ProcessShippingLabel().
        /// </summary>
        public bool IsCancelled { get; private set; }

        // -----------------------------------------------------------------------
        // Construction
        // -----------------------------------------------------------------------

        public ShippingLabel(AccessInfo info, int invoice, string order, string shipText)
        {
            m_Info = info;
            m_iInvoice = invoice;
            m_strOrder = order;
            m_strShippingText = shipText;
            m_strTransactionStatus = string.Empty;
            m_strTransactionData = string.Empty;

            SubmitShippingLabelRequest();
        }

        // -----------------------------------------------------------------------
        // Public entry point
        // -----------------------------------------------------------------------

        public bool RunLabelOperations()
        {
            // Wait for the transaction to leave "Processing" state
            int trxStatus = GetTransactionStatus();
            while (trxStatus == 1)
            {
                Thread.Sleep(1000);
                trxStatus = GetTransactionStatus();
            }

            if (trxStatus == 2)
                return false;

            int labelResult = GetShippingLabelRequest();

            if (labelResult == 0)
                return DecodeAndObtainTracking();

            if (labelResult == 1)
            {
                Logger.Error(
                    "Transaction failed for order {0} / invoice {1}. " +
                    "Shipping method may have been re-assigned.",
                    m_strOrder, m_iInvoice);
                return false;
            }

            if (labelResult == 2)
            {
                Logger.Error(
                    "Bad payload for order {0} / invoice {1}. Retrying...",
                    m_strOrder, m_iInvoice);

                for (int attempt = 0; attempt < 4; attempt++)
                {
                    Thread.Sleep(2500);
                    int retry = GetShippingLabelRequest();

                    if (retry == 0)
                        return DecodeAndObtainTracking();

                    if (retry == 1)
                    {
                        Logger.Error(
                            "Transaction failed on retry for order {0} / invoice {1}.",
                            m_strOrder, m_iInvoice);
                        return false;
                    }

                    Thread.Sleep(1500);
                }

                if (m_strPayload?.Length < 4000)
                {
                    Logger.Error(
                        "GetShippingLabelRequest failed on all retries for " +
                        "order {0} / invoice {1}.",
                        m_strOrder, m_iInvoice);
                    return false;
                }
            }

            if (labelResult == 3)
            {
                Logger.Error(
                    "Too many bad payloads for order {0} / invoice {1}.",
                    m_strOrder, m_iInvoice);
                return false;
            }

            Logger.Error(
                "Unhandled label result {0} for order {1} / invoice {2}.",
                labelResult, m_strOrder, m_iInvoice);
            return false;
        }

        // -----------------------------------------------------------------------
        // Step 1 — submit label request
        // -----------------------------------------------------------------------

        private bool SubmitShippingLabelRequest()
        {
            var client = new RestClient("https://sellingpartnerapi-na.amazon.com");
            IRestRequest request = new RestRequest(
                "vendor/directFulfillment/shipping/v1/shippingLabels", Method.POST);

            var body = new ShippingLabelRequestList
            {
                shippingLabelRequests = new List<ShippingLabelRequests>
                {
                    new ShippingLabelRequests
                    {
                        purchaseOrderNumber = m_strOrder,
                        shipFromParty       = new ShipFromParty { partyId = m_Info.Warehouse },
                        sellingParty        = new SellingParty  { partyId = m_Info.SellingParty }
                    }
                }
            };

            request.AddJsonBody(JsonConvert.SerializeObject(body));
            request.AddHeader("x-amz-access-token", m_Info.AccessToken);

            var helper = new AWSSignerHelper();
            request.AddHeader("x-amz-content-sha256", helper.HashRequestBody(request));
            request = new AWSSigV4Signer(m_Info.AWSCredentials)
                .Sign(request, client.BaseUrl.Host);

            var response = client.Execute(request);

            dynamic results = JsonConvert.DeserializeObject<dynamic>(response.Content);
            m_strTransactionId = results?.payload?.transactionId?.ToString() ?? string.Empty;
            m_dtSubmissionRequest = DateTime.Now;

            Logger.Info(
                "Submission request for order {0} succeeded. Transaction id: {1}.",
                m_strOrder, m_strTransactionId);

            return m_strTransactionId.Length > 1;
        }

        // -----------------------------------------------------------------------
        // Step 2 — poll transaction status
        //   Returns: 0 = ok/processing  1 = still processing (retry)  2 = fatal
        // -----------------------------------------------------------------------

        private int GetTransactionStatus()
        {
            var client = new RestClient("https://sellingpartnerapi-na.amazon.com");
            IRestRequest request = new RestRequest(
                $"vendor/directFulfillment/transactions/v1/transactions/{m_strTransactionId}",
                Method.GET);

            request.AddHeader("x-amz-access-token", m_Info.AccessToken);
            request = new AWSSigV4Signer(m_Info.AWSCredentials)
                .Sign(request, client.BaseUrl.Host);

            var response = client.Execute(request);
            string raw = Convert.ToString(
                JsonConvert.DeserializeObject<dynamic>(response.Content));

            if (raw.ToLower().Contains("resourcenotfound"))
                return 0;

            dynamic obj = JsonConvert.DeserializeObject<dynamic>(response.Content);

            // Save transaction payload to disk (skip while still processing)
            if (m_bSaveTransactions && !raw.ToLower().Contains("processing"))
                SaveTransactionFile(raw);

            foreach (var obj1 in obj)
            {
                dynamic myobj = obj1.First;
                m_strTransactionData = Convert.ToString(myobj);
                m_strTransactionStatus = Convert.ToString(myobj.transactionStatus.status);

                if (m_strTransactionData.ToLower().Contains("internalfailure"))
                {
                    Logger.Error(
                        "Internal failure in transaction for order {0} / invoice {1}: {2}",
                        m_strOrder, m_iInvoice, m_strTransactionData);
                    return 2;
                }

                if (m_strTransactionStatus.ToLower() == "failure")
                {
                    CheckForCancellation(myobj);
                    // Cancellation is confirmed (or not) — either way unrecoverable
                    return 2;
                }

                if (m_strTransactionData != string.Empty)
                    break;
            }

            return 0;
        }

        /// <summary>
        /// Inspects the errors array in a Failure response.
        /// When INVALID_ORDER_STATUS with "cancelled" is found, calls the Orders
        /// API to confirm, and sets <see cref="IsCancelled"/> accordingly.
        /// </summary>
        private void CheckForCancellation(dynamic transactionStatus)
        {
            try
            {
                var errors = transactionStatus.transactionStatus.errors;
                if (errors == null) return;

                foreach (var error in errors)
                {
                    string code = error.code?.ToString() ?? string.Empty;
                    string message = error.message?.ToString() ?? string.Empty;

                    Logger.Warn(
                        "Transaction error for order {0} / invoice {1} — " +
                        "code: {2}, message: {3}",
                        m_strOrder, m_iInvoice, code, message);

                    if (code == "INVALID_ORDER_STATUS" &&
                        message.ToLower().Contains("cancelled"))
                    {
                        Logger.Warn(
                            "Cancellation indicated for order {0} / invoice {1}. " +
                            "Verifying with Orders API...",
                            m_strOrder, m_iInvoice);

                        IsCancelled = VerifyOrderCancellation();

                        Logger.Log(
                            IsCancelled ? LogLevel.Warn : LogLevel.Error,
                            IsCancelled
                                ? "Order {0} / invoice {1} confirmed cancelled by Orders API."
                                : "Could not confirm cancellation for order {0} / invoice {1} " +
                                  "— Orders API did not return CANCELLED status.",
                            m_strOrder, m_iInvoice);

                        return; // one cancellation error is enough
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex,
                    "Exception while checking cancellation errors for " +
                    "order {0} / invoice {1}.", m_strOrder, m_iInvoice);
            }
        }

        /// <summary>
        /// Calls the Direct Fulfillment Orders API to confirm the real order status.
        /// Returns true only when the API explicitly reports "CANCELLED".
        /// </summary>
        private bool VerifyOrderCancellation()
        {
            try
            {
                var client = new RestClient("https://sellingpartnerapi-na.amazon.com");
                IRestRequest request = new RestRequest(
                    $"vendor/directFulfillment/orders/v1/purchaseOrders/{m_strOrder}",
                    Method.GET);

                request.AddHeader("x-amz-access-token", m_Info.AccessToken);
                request = new AWSSigV4Signer(m_Info.AWSCredentials)
                    .Sign(request, client.BaseUrl.Host);

                IRestResponse response = client.Execute(request);

                if (!response.IsSuccessful)
                {
                    Logger.Error(
                        "Orders API request failed for order {0} / invoice {1}. " +
                        "HTTP {2}: {3}",
                        m_strOrder, m_iInvoice,
                        (int)response.StatusCode, response.ErrorMessage);
                    return false;
                }

                dynamic result = JsonConvert.DeserializeObject<dynamic>(response.Content);
                string orderStatus = result?.payload?.orderDetails?.orderStatus?.ToString()
                                      ?? string.Empty;

                Logger.Info(
                    "Orders API returned status '{0}' for order {1} / invoice {2}.",
                    orderStatus, m_strOrder, m_iInvoice);

                return string.Equals(orderStatus, "CANCELLED",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Logger.Error(ex,
                    "Exception calling Orders API for order {0} / invoice {1}.",
                    m_strOrder, m_iInvoice);
                return false;
            }
        }

        // -----------------------------------------------------------------------
        // Step 3 — fetch the label
        //   Returns: 0 = success  1 = transaction failure  2 = bad payload
        //            3 = gave up (too many bad payloads)   4 = other error
        // -----------------------------------------------------------------------

        private int GetShippingLabelRequest()
        {
            while (m_strTransactionStatus.ToLower() != "success")
            {
                if (m_strTransactionStatus.ToLower().Contains("failure"))
                {
                    // Attempt a ship-method fallback for Amazon-method orders
                    if (m_strShippingText.ToLower().StartsWith("am"))
                    {
                        string fallback = m_Info.Center == eCenter.C_3DROX_ADWL
                            ? "USPS First Class Mail"
                            : "UPS Ground Residential";

                        Logger.Error(
                            "Transaction {0} failed for order {1} / invoice {2}. " +
                            "Attempting fallback to '{3}'.",
                            m_strTransactionId, m_strOrder, m_iInvoice, fallback);

                        Database.Instance.UpdateShipMethod(m_iInvoice, fallback);
                    }
                    else
                    {
                        Logger.Error(
                            "Transaction {0} failed for order {1} / invoice {2}. " +
                            "Shipping method '{3}' not eligible for fallback.",
                            m_strTransactionId, m_strOrder, m_iInvoice, m_strShippingText);
                    }
                    return 1;
                }

                Logger.Warn(
                    "Transaction {0} status is '{1}' for invoice {2} / order {3}. " +
                    "Waiting for 'Success'...",
                    m_strTransactionId, m_strTransactionStatus,
                    m_iInvoice, m_strOrder);

                Thread.Sleep(2000);

                if (GetTransactionStatus() == 2)
                    return 4;
            }

            Logger.Info(
                "Transaction status for invoice {0} is now '{1}'.",
                m_iInvoice, m_strTransactionStatus);

            OrderManager.Instance.AddToLogTextBox(
                $"Transaction for invoice {m_iInvoice} succeeded.");

            // Fetch the actual label
            var client = new RestClient("https://sellingpartnerapi-na.amazon.com");
            IRestRequest request = new RestRequest(
                $"vendor/directFulfillment/shipping/v1/shippingLabels/{m_strOrder}",
                Method.GET);

            request.AddHeader("x-amz-access-token", m_Info.AccessToken);
            request = new AWSSigV4Signer(m_Info.AWSCredentials)
                .Sign(request, client.BaseUrl.Host);

            var response = client.Execute(request);

            dynamic results = JsonConvert.DeserializeObject<dynamic>(response.Content);

            if (results.errors != null && results.errors.code != null)
            {
                Logger.Error(
                    "Errors in payload for order {0} / invoice {1}: {2}",
                    m_strOrder, m_iInvoice, response.Content);
                return 4;
            }

            m_strPayload = results.payload?.ToString() ?? string.Empty;
            SavePayloadFile();

            Logger.Info(
                "Payload length for order {0} / invoice {1}: {2} bytes.",
                m_strOrder, m_iInvoice, m_strPayload.Length);

            if (m_strPayload.Length < SmallPayloadBytes)
            {
                Logger.Error(
                    "Payload under {0} bytes for order {1} / invoice {2}. " +
                    "Payload saved to: {3}",
                    SmallPayloadBytes, m_strOrder, m_iInvoice, m_strPayloadFile);

                if (++m_iAttempts > MaxPayloadRetries)
                    return 3;

                return 2;
            }

            return 0;
        }

        // -----------------------------------------------------------------------
        // Step 4 — decode ZPL and extract tracking
        // -----------------------------------------------------------------------

        private bool DecodeAndObtainTracking()
        {
            if (!DecodeZPL())
            {
                Logger.Error(
                    "Failed to decode ZPL for order {0} / invoice {1}.",
                    m_strOrder, m_iInvoice);
                return false;
            }
            if (!ObtainTracking())
            {
                Logger.Error(
                    "Failed to extract tracking for order {0} / invoice {1}.",
                    m_strOrder, m_iInvoice);
                return false;
            }

            Logger.Info(
                "Successfully obtained ZPL and tracking for order {0} / invoice {1}.",
                m_strOrder, m_iInvoice);
            return true;
        }

        private bool DecodeZPL()
        {
            if (string.IsNullOrEmpty(m_strPayload))
            {
                Logger.Error(
                    "Cannot decode ZPL for order {0} / invoice {1}: payload is null.",
                    m_strOrder, m_iInvoice);
                return false;
            }

            dynamic results = JsonConvert.DeserializeObject<dynamic>(m_strPayload);
            string raw = results.labelData[0].content.Value;
            m_strLabelContent = raw;

            byte[] data = Convert.FromBase64String(raw);
            m_strZPL = Encoding.UTF8.GetString(data);

            ValidateZPL();
            return true;
        }

        private void ValidateZPL()
        {
            if (string.IsNullOrEmpty(m_strZPL)) return;

            int count = CountOccurrences(m_strZPL, "^XA");

            if (count == 1)
            {
                Logger.Info(
                    "ZPL validated (1 ^XA tag) for order {0} / invoice {1}.",
                    m_strOrder, m_iInvoice);
                return;
            }

            if (count == 2)
            {
                Logger.Info(
                    "Found 2 ^XA tags for order {0} / invoice {1}. Removing first set.",
                    m_strOrder, m_iInvoice);

                var sb = new StringBuilder();
                string[] lines = m_strZPL.Split(
                    new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

                foreach (string line in lines)
                {
                    if (line.StartsWith("^FX Start") ||
                        line.StartsWith("^XA^MCY^XZ"))
                        continue;

                    sb.AppendLine(line);
                }
                m_strZPL = sb.ToString();
            }
            else
            {
                Logger.Warn(
                    "Found {0} ^XA tag(s) in ZPL for order {1} / invoice {2}.",
                    count, m_strOrder, m_iInvoice);
            }
        }

        private bool ObtainTracking()
        {
            if (string.IsNullOrEmpty(m_strPayload))
            {
                Logger.Error(
                    "Cannot obtain tracking for order {0} / invoice {1}: payload is null.",
                    m_strOrder, m_iInvoice);
                return false;
            }

            dynamic results = JsonConvert.DeserializeObject<dynamic>(m_strPayload);
            m_strTracking = results.labelData[0].trackingNumber.Value;
            m_strShipMethod = results.labelData[0].shipMethod.Value;
            m_strShipMethodName = results.labelData[0].shipMethodName.Value;
            return true;
        }

        // -----------------------------------------------------------------------
        // Carrier helper
        // -----------------------------------------------------------------------

        public string GetCarrierShortName()
        {
            string name = m_strShipMethodName?.ToLower() ?? string.Empty;

            if (name.StartsWith("amazon") || name.StartsWith("amzl")) return "AMZAT";
            if (name.StartsWith("ups")) return "AMZUPS";
            if (name.StartsWith("usps")) return "AMZUSPS";
            return "AMZUNK";
        }

        // -----------------------------------------------------------------------
        // File helpers
        // -----------------------------------------------------------------------

        private void SaveTransactionFile(string content)
        {
            string path = NextAvailablePath(
                $"trx\\{m_iInvoice}-{m_strOrder}-{{NUM}}.txt");
            try
            {
                File.WriteAllText(path, content);
                Logger.Info(
                    "Saved transaction file for order {0} / invoice {1} to {2}.",
                    m_strOrder, m_iInvoice, path);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to save transaction file to {0}.", path);
            }
        }

        private void SavePayloadFile()
        {
            if (!m_bSavePayloads) return;

            string path = NextAvailablePath(
                $"payloads\\{m_iInvoice}-{m_strOrder}-{{NUM}}.txt");
            m_strPayloadFile = path;
            try
            {
                File.WriteAllText(path,
                    $"Transaction ID: {m_strTransactionId}{Environment.NewLine}{m_strPayload}");
                Logger.Info(
                    "Saved payload for order {0} / invoice {1} to {2}.",
                    m_strOrder, m_iInvoice, path);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to save payload file to {0}.", path);
            }
        }

        private static string NextAvailablePath(string template)
        {
            string path = template.Replace("{NUM}", "01");
            int n = 1;
            while (File.Exists(path))
                path = template.Replace("{NUM}", (++n).ToString("00"));
            return path;
        }

        private static int CountOccurrences(string source, string sub,
            StringComparison comp = StringComparison.CurrentCulture)
        {
            int count = 0, index = source.IndexOf(sub, comp);
            while (index != -1)
            {
                count++;
                index = source.IndexOf(sub, index + sub.Length, comp);
            }
            return count;
        }
    }
}