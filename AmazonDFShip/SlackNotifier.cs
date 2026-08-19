using System;
using System.Configuration;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RestSharp;
using NLog;

namespace AmazonDFShip
{
    /// <summary>
    /// Fire-and-forget Slack webhook poster.
    /// All methods are fully exception-safe and never block the caller.
    /// Configure the webhook URL via App.config key "Slack.WebhookUrl".
    /// </summary>
    internal static class SlackNotifier
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // Resolved once at startup; empty string disables all notifications silently.
        private static readonly string WebhookUrl =
            ConfigurationManager.AppSettings["Slack.WebhookUrl"] ?? string.Empty;

        /// <summary>
        /// Posts <paramref name="text"/> to the configured Slack channel
        /// asynchronously.  Returns immediately; never throws.
        /// </summary>
        public static void Post(string text)
        {
            if (string.IsNullOrWhiteSpace(WebhookUrl))
                return;

            // Capture for the lambda — text is immutable so no defensive copy needed.
            Task.Run(() => SendInternal(text));
        }

        private static void SendInternal(string text)
        {
            try
            {
                // Slack incoming-webhook endpoints are full URLs, so we split the
                // base URL from the path so RestSharp can sign the request correctly.
                var uri = new Uri(WebhookUrl);
                var client = new RestClient($"{uri.Scheme}://{uri.Host}");
                var req = new RestRequest(uri.PathAndQuery, Method.POST);

                req.AddJsonBody(JsonConvert.SerializeObject(new { text }));

                IRestResponse resp = client.Execute(req);

                if (!resp.IsSuccessful)
                {
                    Logger.Warn(
                        "Slack notification returned non-success status {0}: {1}",
                        (int)resp.StatusCode, resp.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                // Swallow everything — Slack is non-critical infrastructure.
                Logger.Warn(ex, "Slack notification failed (non-fatal).");
            }
        }
    }
}