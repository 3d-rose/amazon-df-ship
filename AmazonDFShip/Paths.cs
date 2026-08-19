using System;
using System.IO;
using NLog;

namespace AmazonDFShip
{
    /// <summary>
    /// Resolves every file the application writes against the directory the
    /// executable lives in, and creates the directory if it is missing.
    ///
    /// Why this exists: the app used bare relative paths ("temp\\1234.zpl",
    /// "trx\\...", "payloads\\...", "invoice_exclusions.txt").  Relative paths are
    /// resolved against the *current working directory*, which for a double-clicked
    /// GUI launch happens to be the exe folder, but for a CLI or Task Scheduler
    /// launch is whatever directory the caller was sitting in.  That mismatch made
    /// every ZPL save throw DirectoryNotFoundException under --auto, so every order
    /// was counted as a failure even though Amazon had returned a valid label.
    /// </summary>
    internal static class Paths
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>Directory containing AmazonDFShip.exe (always ends with a separator).</summary>
        public static string BaseDir { get; } = AppDomain.CurrentDomain.BaseDirectory;

        public const string TempDirName = "temp";
        public const string TransactionDirName = "trx";
        public const string PayloadDirName = "payloads";

        /// <summary>
        /// Turns a relative path into an absolute one rooted at <see cref="BaseDir"/>.
        /// Absolute paths are returned unchanged.
        /// </summary>
        public static string Resolve(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            return Path.IsPathRooted(path)
                ? path
                : Path.Combine(BaseDir, path);
        }

        /// <summary>
        /// Resolves <paramref name="path"/> and makes sure the directory that will
        /// contain it exists.  Returns the absolute path.  Throws only if the
        /// directory genuinely cannot be created, which callers already handle.
        /// </summary>
        public static string ResolveForWrite(string path)
        {
            string full = Resolve(path);
            string dir = Path.GetDirectoryName(full);

            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                Logger.Info("Created missing output directory: {0}", dir);
            }

            return full;
        }

        /// <summary>
        /// Creates the working directories the app writes into during a run.
        /// Called once at startup so a headless run fails loudly here rather than
        /// silently on the first order.
        /// </summary>
        public static void EnsureWorkingDirectories()
        {
            foreach (string name in new[] { TempDirName, TransactionDirName, PayloadDirName, "logs" })
            {
                string dir = Path.Combine(BaseDir, name);
                if (Directory.Exists(dir)) continue;

                Directory.CreateDirectory(dir);
                Logger.Info("Created missing working directory: {0}", dir);
            }
        }

        /// <summary>
        /// Verifies the shipping-label output directory from tbADSConfig is usable.
        /// An empty value previously produced paths like "\\1234.zpl", which resolve
        /// to the root of the current drive and fail with UnauthorizedAccessException.
        /// </summary>
        public static bool ValidateLabelPath(string labelPath, out string error)
        {
            if (string.IsNullOrWhiteSpace(labelPath))
            {
                error = "The shipping-label output directory is empty. Check that " +
                        "configname 'ShippingLabelDir2' exists in tbADSConfig and the " +
                        "database login has permission to read it.";
                return false;
            }

            try
            {
                if (!Directory.Exists(labelPath))
                {
                    Directory.CreateDirectory(labelPath);
                    Logger.Info("Created shipping-label output directory: {0}", labelPath);
                }
            }
            catch (Exception ex)
            {
                error = $"Shipping-label output directory '{labelPath}' is not usable: {ex.Message}";
                return false;
            }

            error = null;
            return true;
        }
    }
}
