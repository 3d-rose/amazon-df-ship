namespace AmazonDFShip
{
    partial class frmMain
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.lblBatches = new System.Windows.Forms.Label();
            this.clbBatches = new System.Windows.Forms.CheckedListBox();
            this.lblOrderCount11 = new System.Windows.Forms.Label();
            this.lblOrderCount20 = new System.Windows.Forms.Label();
            this.btnGenerateShippingLabels = new System.Windows.Forms.Button();
            this.btnRefresh = new System.Windows.Forms.Button();
            this.label1 = new System.Windows.Forms.Label();
            this.lblProcessing = new System.Windows.Forms.Label();
            this.btnTest = new System.Windows.Forms.Button();
            this.txtLog = new System.Windows.Forms.RichTextBox();
            this.lblSuccess = new System.Windows.Forms.Label();
            this.lblFailures = new System.Windows.Forms.Label();
            this.btnExit = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // lblBatches
            // 
            this.lblBatches.AutoSize = true;
            this.lblBatches.Location = new System.Drawing.Point(10, 15);
            this.lblBatches.Name = "lblBatches";
            this.lblBatches.Size = new System.Drawing.Size(250, 13);
            this.lblBatches.TabIndex = 0;
            this.lblBatches.Text = "Select batches to generate UPS shipping labels for:";
            // 
            // clbBatches
            // 
            this.clbBatches.CheckOnClick = true;
            this.clbBatches.FormattingEnabled = true;
            this.clbBatches.Location = new System.Drawing.Point(10, 40);
            this.clbBatches.Name = "clbBatches";
            this.clbBatches.Size = new System.Drawing.Size(171, 94);
            this.clbBatches.TabIndex = 1;
            this.clbBatches.Click += new System.EventHandler(this.clbBatches_Click);
            // 
            // lblOrderCount11
            // 
            this.lblOrderCount11.AutoSize = true;
            this.lblOrderCount11.Location = new System.Drawing.Point(230, 40);
            this.lblOrderCount11.Name = "lblOrderCount11";
            this.lblOrderCount11.Size = new System.Drawing.Size(96, 13);
            this.lblOrderCount11.TabIndex = 2;
            this.lblOrderCount11.Text = "Orders for 3DROX:";
            // 
            // lblOrderCount20
            // 
            this.lblOrderCount20.AutoSize = true;
            this.lblOrderCount20.Location = new System.Drawing.Point(230, 60);
            this.lblOrderCount20.Name = "lblOrderCount20";
            this.lblOrderCount20.Size = new System.Drawing.Size(95, 13);
            this.lblOrderCount20.TabIndex = 3;
            this.lblOrderCount20.Text = "Orders for 3DRPB:";
            // 
            // btnGenerateShippingLabels
            // 
            this.btnGenerateShippingLabels.Location = new System.Drawing.Point(10, 375);
            this.btnGenerateShippingLabels.Name = "btnGenerateShippingLabels";
            this.btnGenerateShippingLabels.Size = new System.Drawing.Size(141, 23);
            this.btnGenerateShippingLabels.TabIndex = 5;
            this.btnGenerateShippingLabels.Text = "Generate Shipping Labels";
            this.btnGenerateShippingLabels.UseVisualStyleBackColor = true;
            this.btnGenerateShippingLabels.Click += new System.EventHandler(this.btnGenerateShippingLabels_Click);
            // 
            // btnRefresh
            // 
            this.btnRefresh.Location = new System.Drawing.Point(260, 375);
            this.btnRefresh.Name = "btnRefresh";
            this.btnRefresh.Size = new System.Drawing.Size(98, 23);
            this.btnRefresh.TabIndex = 6;
            this.btnRefresh.Text = "Refresh Batches";
            this.btnRefresh.UseVisualStyleBackColor = true;
            this.btnRefresh.Click += new System.EventHandler(this.btnRefresh_Click);
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(13, 157);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(72, 13);
            this.label1.TabIndex = 7;
            this.label1.Text = "Realtime Log:";
            // 
            // lblProcessing
            // 
            this.lblProcessing.AutoSize = true;
            this.lblProcessing.Location = new System.Drawing.Point(230, 83);
            this.lblProcessing.Name = "lblProcessing";
            this.lblProcessing.Size = new System.Drawing.Size(52, 13);
            this.lblProcessing.TabIndex = 8;
            this.lblProcessing.Text = "Waiting...";
            // 
            // btnTest
            // 
            this.btnTest.Location = new System.Drawing.Point(532, 5);
            this.btnTest.Name = "btnTest";
            this.btnTest.Size = new System.Drawing.Size(75, 23);
            this.btnTest.TabIndex = 9;
            this.btnTest.Text = "Test";
            this.btnTest.UseVisualStyleBackColor = true;
            this.btnTest.Visible = false;
            this.btnTest.Click += new System.EventHandler(this.btnTest_Click);
            // 
            // txtLog
            // 
            this.txtLog.BackColor = System.Drawing.SystemColors.ControlLightLight;
            this.txtLog.Location = new System.Drawing.Point(10, 173);
            this.txtLog.Name = "txtLog";
            this.txtLog.ReadOnly = true;
            this.txtLog.Size = new System.Drawing.Size(600, 190);
            this.txtLog.TabIndex = 10;
            this.txtLog.Text = "";
            // 
            // lblSuccess
            // 
            this.lblSuccess.AutoSize = true;
            this.lblSuccess.Location = new System.Drawing.Point(230, 103);
            this.lblSuccess.Name = "lblSuccess";
            this.lblSuccess.Size = new System.Drawing.Size(52, 13);
            this.lblSuccess.TabIndex = 11;
            this.lblSuccess.Text = "Waiting...";
            // 
            // lblFailures
            // 
            this.lblFailures.AutoSize = true;
            this.lblFailures.Location = new System.Drawing.Point(230, 123);
            this.lblFailures.Name = "lblFailures";
            this.lblFailures.Size = new System.Drawing.Size(52, 13);
            this.lblFailures.TabIndex = 12;
            this.lblFailures.Text = "Waiting...";
            // 
            // btnExit
            // 
            this.btnExit.Location = new System.Drawing.Point(532, 375);
            this.btnExit.Name = "btnExit";
            this.btnExit.Size = new System.Drawing.Size(75, 23);
            this.btnExit.TabIndex = 13;
            this.btnExit.Text = "Exit";
            this.btnExit.UseVisualStyleBackColor = true;
            this.btnExit.Click += new System.EventHandler(this.btnExit_Click);
            // 
            // frmMain
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(619, 406);
            this.Controls.Add(this.btnExit);
            this.Controls.Add(this.lblFailures);
            this.Controls.Add(this.lblSuccess);
            this.Controls.Add(this.txtLog);
            this.Controls.Add(this.btnTest);
            this.Controls.Add(this.lblProcessing);
            this.Controls.Add(this.label1);
            this.Controls.Add(this.btnRefresh);
            this.Controls.Add(this.btnGenerateShippingLabels);
            this.Controls.Add(this.lblOrderCount20);
            this.Controls.Add(this.lblOrderCount11);
            this.Controls.Add(this.clbBatches);
            this.Controls.Add(this.lblBatches);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.Name = "frmMain";
            this.Text = "Amazon DS - Shipping Label Generator";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.frmMain_FormClosing);
            this.FormClosed += new System.Windows.Forms.FormClosedEventHandler(this.frmMain_FormClosed);
            this.Load += new System.EventHandler(this.frmMain_Load);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label lblBatches;
        private System.Windows.Forms.CheckedListBox clbBatches;
        private System.Windows.Forms.Label lblOrderCount11;
        private System.Windows.Forms.Label lblOrderCount20;
        private System.Windows.Forms.Button btnGenerateShippingLabels;
        private System.Windows.Forms.Button btnRefresh;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Label lblProcessing;
        private System.Windows.Forms.Button btnTest;
        private System.Windows.Forms.RichTextBox txtLog;
        private System.Windows.Forms.Label lblSuccess;
        private System.Windows.Forms.Label lblFailures;
        private System.Windows.Forms.Button btnExit;
    }
}

