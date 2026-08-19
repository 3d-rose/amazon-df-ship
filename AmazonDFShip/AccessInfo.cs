using System;
using System.Collections.Generic;
using System.Configuration;
using Amazon.SellingPartnerAPIAA;
using Newtonsoft.Json;
using RestSharp;
using NLog;

namespace AmazonDFShip
{
    // ---------------------------------------------------------------------------
    // Amazon SP-API DTOs — shared by AccessInfo and ShippingLabel
    // ---------------------------------------------------------------------------

    public class ShippingLabelRequestList
    {
        public List<ShippingLabelRequests> shippingLabelRequests { get; set; }
    }

    public class ShippingLabelRequests
    {
        public string purchaseOrderNumber { get; set; }
        public ShipFromParty shipFromParty { get; set; }
        public SellingParty sellingParty { get; set; }
    }

    public class SellingParty { public string partyId { get; set; } }
    public class ShipFromParty { public string partyId { get; set; } }

    public class RestrictedResourcesList
    {
        public List<ResourceList> restrictedResources { get; set; }
    }

    public class ResourceList
    {
        public string method { get; set; }
        public string path { get; set; }
    }

    // ---------------------------------------------------------------------------

    internal enum eCenter
    {
        C_3DROX_ADWL = 1,
        C_3DRPB_AOTW = 2
    }

    internal class AccessInfo
    {
        // -----------------------------------------------------------------------
        // Fields
        // -----------------------------------------------------------------------

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly eCenter _center;
        private readonly int _siteId;
        private readonly string _sellingParty;
        private readonly string _warehouse;
        private readonly AWSAuthenticationCredentials _awsCredentials;

        private string _accessToken;
        private string _restrictedDataToken;
        private DateTime _tokenRetrievalTime;
        private bool _initialized;

        // Guards concurrent Refresh() calls from different threads
        private readonly object _refreshLock = new object();

        // -----------------------------------------------------------------------
        // Properties
        // -----------------------------------------------------------------------

        public eCenter Center => _center;
        public bool Initialized => _initialized;
        public string SellingParty => _sellingParty;
        public string Warehouse => _warehouse;
        public DateTime TokenRetrievalTime => _tokenRetrievalTime;

        public AWSAuthenticationCredentials AWSCredentials => _awsCredentials;
        public string AccessToken => _accessToken;
        public string RestrictedDataToken => _restrictedDataToken;

        // -----------------------------------------------------------------------
        // Construction — private; callers use the factory method
        // -----------------------------------------------------------------------

        private AccessInfo(eCenter center)
        {
            _center = center;

            switch (center)
            {
                case eCenter.C_3DROX_ADWL:
                    _sellingParty = "3DROX";
                    _warehouse = "ADWL";
                    _siteId = 11;
                    break;

                case eCenter.C_3DRPB_AOTW:
                    _sellingParty = "3DRPB";
                    _warehouse = "AOTW";
                    _siteId = 20;
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(center),
                        $"Unsupported center value: {center}.");
            }

            _awsCredentials = LoadAwsCredentials();
        }

        /// <summary>
        /// Creates and fully initialises an <see cref="AccessInfo"/> instance.
        /// Returns <c>null</c> (and logs the reason) when initialisation fails,
        /// so callers never receive a partially-constructed object.
        /// </summary>
        public static AccessInfo Create(eCenter center)
        {
            var info = new AccessInfo(center);
            info.Initialize(isRefresh: false);

            if (!info._initialized)
            {
                Logger.Fatal("AccessInfo.Create failed for center {0}. " +
                             "Check preceding log entries.", center);
                return null;
            }

            return info;
        }

        // -----------------------------------------------------------------------
        // Token management
        // -----------------------------------------------------------------------

        /// <summary>
        /// Fetches a new access token and restricted-data token.
        /// Thread-safe: concurrent callers block until the first refresh
        /// completes, then re-use its result instead of double-fetching.
        /// Returns <c>true</c> on success.
        /// </summary>
        public bool Refresh()
        {
            lock (_refreshLock)
            {
                Initialize(isRefresh: true);
                return _initialized;
            }
        }

        private void Initialize(bool isRefresh)
        {
            _initialized = false;

            Logger.Info(isRefresh
                ? "Refreshing tokens for center {0}..."
                : "Initialising AccessInfo for center {0}...",
                _center);

            if (!FetchAccessToken())
            {
                Logger.Fatal("Could not obtain access token for center {0}.", _center);
                return;
            }

            if (!FetchRestrictedDataToken())
            {
                Logger.Fatal("Could not obtain restricted-data token for center {0}.", _center);
                return;
            }

            _tokenRetrievalTime = DateTime.Now;
            _initialized = true;

            Logger.Info("Tokens successfully {0} for center {1}.",
                isRefresh ? "refreshed" : "initialised", _center);
        }

        // -----------------------------------------------------------------------
        // Token fetching
        // -----------------------------------------------------------------------

        private bool FetchAccessToken()
        {
            try
            {
                string clientId = GetRequiredSetting("Amazon.ClientId");
                string refreshToken = GetRequiredSetting($"{_sellingParty}.RefreshToken");
                string clientSecret = ResolveClientSecret();

                var client = new RestClient("https://api.amazon.com");
                var request = new RestRequest("auth/o2/token", Method.POST);
                request.AddJsonBody(new
                {
                    client_id = clientId,
                    client_secret = clientSecret,
                    refresh_token = refreshToken,
                    grant_type = "refresh_token"
                });

                IRestResponse response = client.Execute(request);

                if (!response.IsSuccessful)
                {
                    Logger.Error("Access-token HTTP request failed for center {0}. " +
                                 "Status: {1}. Message: {2}",
                                 _center, (int)response.StatusCode, response.ErrorMessage);
                    return false;
                }

                dynamic result = JsonConvert.DeserializeObject<dynamic>(response.Content);
                string token = result?.access_token?.ToString();

                if (string.IsNullOrEmpty(token))
                {
                    Logger.Error("Access-token response contained no token for center {0}. " +
                                 "Raw response: {1}", _center, response.Content);
                    return false;
                }

                _accessToken = token;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Exception fetching access token for center {0}.", _center);
                return false;
            }
        }

        private bool FetchRestrictedDataToken()
        {
            try
            {
                var body = new RestrictedResourcesList
                {
                    restrictedResources = new List<ResourceList>
                    {
                        new ResourceList
                        {
                            method = "GET",
                            path   = "/vendor/directFulfillment/shipping/v1/shippingLabels"
                        },
                        new ResourceList
                        {
                            method = "GET",
                            path   = "/vendor/directFulfillment/shipping/v1/shippingLabels/{litun}"
                        }
                    }
                };

                var client = new RestClient("https://sellingpartnerapi-na.amazon.com");
                IRestRequest request = new RestRequest(
                    "tokens/2021-03-01/restrictedDataToken", Method.POST);

                request.AddJsonBody(JsonConvert.SerializeObject(body));
                request.AddHeader("x-amz-access-token", _accessToken);

                var signerHelper = new AWSSignerHelper();
                request.AddHeader("x-amz-content-sha256",
                    signerHelper.HashRequestBody(request));

                request = new AWSSigV4Signer(_awsCredentials)
                    .Sign(request, client.BaseUrl.Host);

                IRestResponse response = client.Execute(request);

                if (!response.IsSuccessful)
                {
                    Logger.Error("Restricted-data-token HTTP request failed for center {0}. " +
                                 "Status: {1}. Message: {2}",
                                 _center, (int)response.StatusCode, response.ErrorMessage);
                    return false;
                }

                dynamic result = JsonConvert.DeserializeObject<dynamic>(response.Content);
                string token = result?.restrictedDataToken?.ToString();

                if (string.IsNullOrEmpty(token))
                {
                    Logger.Error("RDT response contained no token for center {0}. " +
                                 "Raw response: {1}", _center, response.Content);
                    return false;
                }

                _restrictedDataToken = token;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Exception fetching restricted-data token for center {0}.",
                    _center);
                return false;
            }
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Tries the database first; falls back to App.config if the DB call
        /// fails or returns empty.  A missing fallback key is a hard error
        /// (throws <see cref="ConfigurationErrorsException"/>).
        /// </summary>
        private string ResolveClientSecret()
        {
            string secret = string.Empty;

            if (Database.Instance.GetClientSecretForSite(_siteId, ref secret)
                && !string.IsNullOrEmpty(secret))
            {
                return secret;
            }

            Logger.Warn("Could not retrieve client secret from the database for " +
                        "site {0} ({1}). Falling back to App.config.", _siteId, _sellingParty);

            return GetRequiredSetting($"{_sellingParty}.ClientSecretFallback");
        }

        private static AWSAuthenticationCredentials LoadAwsCredentials()
        {
            return new AWSAuthenticationCredentials
            {
                AccessKeyId = GetRequiredSetting("AWS.AccessKeyId"),
                SecretKey = GetRequiredSetting("AWS.SecretKey"),
                Region = GetRequiredSetting("AWS.Region")
            };
        }

        /// <summary>
        /// Reads a value from App.config and throws a clear exception if it is
        /// missing or blank, rather than silently propagating a null reference.
        /// </summary>
        private static string GetRequiredSetting(string key)
        {
            string value = ConfigurationManager.AppSettings[key];

            if (string.IsNullOrWhiteSpace(value))
                throw new ConfigurationErrorsException(
                    $"Required App.config key '{key}' is missing or empty. " +
                    "Add it to the <appSettings> section.");

            return value;
        }
    }
}