using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using NLog;

namespace AmazonDFShip
{
    public partial class frmDatabaseLogin : Form
    {
        private frmMain m_Main;
        private Logger m_Logger;
        bool m_bLoginSuccessful;
        string m_strUsername;

        public frmDatabaseLogin(frmMain main)
        {
            InitializeComponent();
            m_Logger = LogManager.GetCurrentClassLogger();
            m_Main = main;
        }

        private void btnLogin_Click(object sender, EventArgs e)
        {
            m_bLoginSuccessful = Database.Instance.Login(txtUsername.Text, txtPassword.Text);
            if (m_bLoginSuccessful)
            {
                m_Logger.Log(LogLevel.Info, "Login successful for user: {0}", txtUsername.Text);
                m_strUsername = txtUsername.Text;
                Close();
            }
            else
            {
                MessageBox.Show(
                    "Login failed for user: " + txtUsername.Text + Environment.NewLine +
                    "Please try again.",
                    "Login Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            m_bLoginSuccessful = false;
            Close();
        }

        public bool LoginSuccessful() => m_bLoginSuccessful;

        public string LoggedUsername() => m_strUsername;

        /// <summary>
        /// Authenticates without showing the dialog — used in headless (--auto) mode.
        /// Credentials are supplied via App.config keys DB.Username and DB.Password.
        /// Returns true on success; logs and returns false on failure.
        /// </summary>
        public bool TryAutoLogin(string username, string password)
        {
            m_bLoginSuccessful = Database.Instance.Login(username, password);

            if (m_bLoginSuccessful)
            {
                m_strUsername = username;
                m_Logger.Info("Headless auto-login successful for user: {0}", username);
            }
            else
            {
                m_Logger.Fatal(
                    "Headless auto-login failed for user: {0}. " +
                    "Check DB.Username and DB.Password in App.config.", username);
            }

            return m_bLoginSuccessful;
        }
    }
}