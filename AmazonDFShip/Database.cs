using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using NLog;

namespace AmazonDFShip
{
    class Database
    {
        // -----------------------------------------------------------------------
        // Singleton
        // -----------------------------------------------------------------------

        private static Database m_Instance;
        private static readonly object s_Padlock = new object();

        public static Database Instance
        {
            get
            {
                lock (s_Padlock)
                {
                    return m_Instance ?? (m_Instance = new Database());
                }
            }
        }

        // -----------------------------------------------------------------------
        // Fields
        // -----------------------------------------------------------------------

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private string m_strConnectionString;

        private const string DataSource = "fe80::68d9:775b:28fb:8550";
        private const string InitialCatalog = "3DRose";

        // -----------------------------------------------------------------------
        // Construction
        // -----------------------------------------------------------------------

        private Database() { }

        // -----------------------------------------------------------------------
        // Login
        // -----------------------------------------------------------------------

        public bool Login(string username, string password)
        {
            if (string.IsNullOrEmpty(username))
            {
                Logger.Fatal("Database login called with an empty username. " +
                             "In --auto mode this means App.config key 'DB.Username' " +
                             "is missing or blank.");
                return false;
            }

            if (string.IsNullOrEmpty(password))
            {
                Logger.Fatal("Database login called with an empty password. " +
                             "In --auto mode this means App.config key 'DB.Password' " +
                             "is missing or blank.");
                return false;
            }

            // Build the connection string through SqlConnectionStringBuilder rather than
            // by string substitution.  The builder quotes and escapes values, so passwords
            // containing  @  ;  =  '  "  or leading/trailing whitespace survive intact.
            // Naive concatenation silently corrupted such passwords, which is why the
            // interactive login could succeed while --auto failed with the same account.
            try
            {
                var builder = new SqlConnectionStringBuilder
                {
                    DataSource = DataSource,
                    InitialCatalog = InitialCatalog,
                    UserID = username,
                    Password = password,
                    IntegratedSecurity = false
                };

                m_strConnectionString = builder.ConnectionString;
            }
            catch (ArgumentException ex)
            {
                Logger.Fatal(ex, "Could not build a valid SQL connection string for user '{0}'.",
                    username);
                return false;
            }

            using (var connection = new SqlConnection(m_strConnectionString))
            {
                try
                {
                    connection.Open();
                    return connection.State == System.Data.ConnectionState.Open;
                }
                catch (SqlException ex)
                {
                    Logger.Error(ex, "SQL Exception during login for user '{0}'.", username);
                    return false;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Unexpected exception during login for user '{0}'.", username);
                    return false;
                }
            }
        }

        /// <summary>
        /// True once <see cref="Login"/> has produced a usable connection string.
        /// Callers use this to fail fast instead of issuing queries against null.
        /// </summary>
        public bool IsConnected => !string.IsNullOrEmpty(m_strConnectionString);

        // -----------------------------------------------------------------------
        // Secrets
        // -----------------------------------------------------------------------

        public bool GetClientSecretForSite(int siteId, ref string secret)
        {
            if (!IsConnected)
            {
                Logger.Error("GetClientSecretForSite({0}) called before a successful login.", siteId);
                return false;
            }

            const string Sql =
                "SELECT s.token FROM tbSites s WHERE s.SiteNbr = @SITEID";

            using (var connection = new SqlConnection(m_strConnectionString))
            {
                try
                {
                    connection.Open();
                    var cmd = new SqlCommand(Sql, connection);
                    cmd.Parameters.AddWithValue("@SITEID", siteId.ToString());

                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.HasRows && reader.Read())
                            secret = reader[0] as string ?? string.Empty;
                    }

                    if (!string.IsNullOrEmpty(secret))
                        return true;

                    Logger.Error(
                        "No token found in tbSites for site {0}.", siteId);
                    return false;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Exception in GetClientSecretForSite({0}).", siteId);
                    return false;
                }
            }
        }

        // -----------------------------------------------------------------------
        // Orders
        // -----------------------------------------------------------------------

        public int GetOrders(ref List<Order> lstOrders, List<int> exclusions = null)
        {
            if (!IsConnected)
            {
                Logger.Error("GetOrders called before a successful login.");
                return 0;
            }

            const string Sql =
                "SELECT TbAuctionInvoice.AuctionInvoice, TbAuctions.AuctionUserName, " +
                "       TbAuctions.AuctionSite, TbAuctionInvoice.ShippingText, " +
                "       TbAuctionInvoice.BatchNbr " +
                "FROM   TbAuctions " +
                "       INNER JOIN TbAuctionInvoice " +
                "           ON TbAuctions.AuctionInvoice = TbAuctionInvoice.AuctionInvoice " +
                "WHERE  TbAuctionInvoice.Tracking IS NULL " +
                "GROUP  BY TbAuctionInvoice.BatchNbr, TbAuctions.AuctionSite, " +
                "          TbAuctionInvoice.AuctionInvoice, TbAuctions.AuctionUserName, " +
                "          TbAuctionInvoice.ShippingText " +
                "HAVING (TbAuctions.AuctionSite = 11 OR TbAuctions.AuctionSite = 20) " +
                "   AND TbAuctionInvoice.AuctionInvoice > 2400000 " +
                "   AND TbAuctionInvoice.BatchNbr IS NOT NULL " +
                "   AND (   TbAuctionInvoice.ShippingText LIKE 'UPS%' " +
                "        OR TbAuctionInvoice.ShippingText LIKE 'AM%' " +
                "        OR TbAuctionInvoice.ShippingText LIKE 'USPS%') " +
                "ORDER  BY TbAuctionInvoice.AuctionInvoice ASC";

            using (var connection = new SqlConnection(m_strConnectionString))
            {
                try
                {
                    connection.Open();
                    using (var reader = new SqlCommand(Sql, connection).ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            int invoice = (int)reader[0];

                            if (exclusions != null && exclusions.Contains(invoice))
                            {
                                Logger.Warn(
                                    "Invoice {0} is in exclusion list. Skipping.", invoice);
                                continue;
                            }

                            string orderid = (string)reader[1];
                            int siteid = (int)(short)reader[2];
                            string shipmethod = (string)reader[3];
                            string batch = (string)reader[4];

                            lstOrders.Add(new Order(invoice, orderid, siteid, shipmethod, batch));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Exception in GetOrders.");
                    return 0;
                }
            }

            return lstOrders.Count;
        }

        // -----------------------------------------------------------------------
        // Tracking
        // -----------------------------------------------------------------------

        public bool UpdateTrackingForInvoice(int invoice, string tracking,
                                             string carrier = "AMZUPS")
        {
            if (!IsConnected)
            {
                Logger.Error("UpdateTrackingForInvoice({0}) called before a successful login.", invoice);
                return false;
            }

            if (string.IsNullOrEmpty(tracking))
            {
                Logger.Error(
                    "Null or empty tracking string supplied for invoice {0}.", invoice);
                return false;
            }

            const string Sql =
                "UPDATE TbAuctionInvoice " +
                "SET    Tracking = @TRACKING, Carrier = @CARRIER " +
                "WHERE  AuctionInvoice = @INVOICE";

            using (var connection = new SqlConnection(m_strConnectionString))
            {
                try
                {
                    connection.Open();
                    var cmd = new SqlCommand(Sql, connection);
                    cmd.Parameters.AddWithValue("@TRACKING", tracking);
                    cmd.Parameters.AddWithValue("@CARRIER", carrier);
                    cmd.Parameters.AddWithValue("@INVOICE", invoice.ToString());

                    int rows = cmd.ExecuteNonQuery();
                    if (rows > 0)
                    {
                        Logger.Info(
                            "Updated tracking for invoice {0}: tracking={1}, carrier={2}.",
                            invoice, tracking, carrier);
                        return true;
                    }

                    Logger.Error(
                        "Update tracking returned 0 rows for invoice {0}.", invoice);
                    return false;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Exception in UpdateTrackingForInvoice({0}).", invoice);
                    return false;
                }
            }
        }

        /// <summary>
        /// Stamps Tracking = 'CANCELLED' so the order is permanently excluded from
        /// future GetOrders queries (which filter WHERE Tracking IS NULL).
        /// </summary>
        public bool MarkOrderCancelled(int invoice)
        {
            Logger.Info("Marking invoice {0} as CANCELLED in the database.", invoice);
            return UpdateTrackingForInvoice(invoice, "CANCELLED", "CANCELLED");
        }

        public bool UpdateShipMethod(int invoice, string newShipMethod = "USPS First Class Mail")
        {
            if (!IsConnected)
            {
                Logger.Error("UpdateShipMethod({0}) called before a successful login.", invoice);
                return false;
            }

            const string Sql =
                "UPDATE TbAuctionInvoice " +
                "SET    ShippingText = @NEWSHIPMETHOD " +
                "WHERE  AuctionInvoice = @INVOICE";

            using (var connection = new SqlConnection(m_strConnectionString))
            {
                try
                {
                    connection.Open();
                    var cmd = new SqlCommand(Sql, connection);
                    cmd.Parameters.AddWithValue("@INVOICE", invoice.ToString());
                    cmd.Parameters.AddWithValue("@NEWSHIPMETHOD", newShipMethod);

                    int rows = cmd.ExecuteNonQuery();
                    if (rows == 1)
                    {
                        Logger.Info(
                            "Updated ship method for invoice {0} to '{1}'.",
                            invoice, newShipMethod);
                        return true;
                    }

                    Logger.Warn(
                        "UpdateShipMethod returned {0} row(s) for invoice {1}.",
                        rows, invoice);
                    return false;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Exception in UpdateShipMethod({0}).", invoice);
                    return false;
                }
            }
        }

        // -----------------------------------------------------------------------
        // Config
        // -----------------------------------------------------------------------

        public string GetLabelPath()
        {
            if (!IsConnected)
            {
                Logger.Error("GetLabelPath called before a successful login.");
                return string.Empty;
            }

            const string Sql =
                "SELECT configvalue FROM tbADSConfig WHERE configname = @CONFIGNAME";

            using (var connection = new SqlConnection(m_strConnectionString))
            {
                try
                {
                    connection.Open();
                    var cmd = new SqlCommand(Sql, connection);
                    cmd.Parameters.AddWithValue("@CONFIGNAME", "ShippingLabelDir2");

                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.HasRows && reader.Read())
                            return reader[0] as string ?? string.Empty;
                    }

                    Logger.Error("ShippingLabelDir2 not found in tbADSConfig.");
                    return string.Empty;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Exception in GetLabelPath.");
                    return string.Empty;
                }
            }
        }

        // -----------------------------------------------------------------------
        // Audit log
        // -----------------------------------------------------------------------

        public void LogShipData(Order order)
        {
            if (!IsConnected)
            {
                Logger.Error("LogShipData called before a successful login.");
                return;
            }

            using (var connection = new SqlConnection(m_strConnectionString))
            {
                try
                {
                    connection.Open();

                    // Mark any pre-existing rows for this order as excluded duplicates
                    var dupes = new List<int>();
                    const string SelectSql =
                        "SELECT idx FROM tbAmazonDFShip " +
                        "WHERE  invoice = @INVOICE AND orderid = @ORDERID AND exclude = 0";

                    using (var reader = new SqlCommand(SelectSql, connection)
                    {
                        Parameters = {
                            new SqlParameter("@INVOICE", order.Invoice.ToString()),
                            new SqlParameter("@ORDERID", order.OrderId)
                        }
                    }.ExecuteReader())
                    {
                        while (reader.Read())
                            dupes.Add((int)reader[0]);
                    }

                    if (dupes.Count > 0)
                    {
                        Logger.Warn(
                            "Found {0} duplicate row(s) in tbAmazonDFShip for " +
                            "order {1} / invoice {2}. Marking as excluded.",
                            dupes.Count, order.OrderId, order.Invoice);

                        const string ExcludeSql =
                            "UPDATE tbAmazonDFShip SET exclude = 1 WHERE idx = @IDX";
                        foreach (int idx in dupes)
                        {
                            var cmd = new SqlCommand(ExcludeSql, connection);
                            cmd.Parameters.AddWithValue("@IDX", idx.ToString());
                            cmd.ExecuteNonQuery();
                        }
                    }

                    const string InsertSql =
                        "INSERT INTO tbAmazonDFShip " +
                        "       (invoice, orderid, shippingtext, shipmethod, " +
                        "        shipmethodname, tracking, labelcontent, zplcontent) " +
                        "VALUES (@INVOICE, @ORDER, @SHIPPINGTEXT, @SHIPMETHOD, " +
                        "        @SHIPMETHODNAME, @TRACKING, @LABELCONTENT, @ZPLCONTENT)";

                    var insert = new SqlCommand(InsertSql, connection);
                    insert.Parameters.AddWithValue("@INVOICE", order.Invoice);
                    insert.Parameters.AddWithValue("@ORDER", order.OrderId);
                    insert.Parameters.AddWithValue("@SHIPPINGTEXT", order.ShippingMethod);
                    insert.Parameters.AddWithValue("@SHIPMETHOD", order.ShippingLabel.ShipMethod);
                    insert.Parameters.AddWithValue("@SHIPMETHODNAME", order.ShippingLabel.ShipMethodName);
                    insert.Parameters.AddWithValue("@TRACKING", order.ShippingLabel.Tracking);
                    insert.Parameters.AddWithValue("@LABELCONTENT", order.ShippingLabel.LabelContent);
                    insert.Parameters.AddWithValue("@ZPLCONTENT", order.ShippingLabel.ZPL);
                    insert.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Exception in LogShipData for invoice {0}.", order.Invoice);
                }
            }
        }
    }
}