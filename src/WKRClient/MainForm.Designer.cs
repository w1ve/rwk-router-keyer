namespace WKRClient;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    // Top config controls
    private Label lblWinKeyerPort;
    private ComboBox cboWinKeyerPort;

    // Transport mode
    private Label lblTransport;
    private ComboBox cboTransport;

    // UDP controls
    private Label lblServerAddress;
    private TextBox txtServerAddress;
    private Label lblServerPort;
    private NumericUpDown nudServerPort;

    // Cloud Relay controls
    private Label lblPairingToken;
    private TextBox txtPairingToken;
    private Label lblRelayStatus;

    private Button btnStart;
    private Button btnStop;

    // TabControl
    private TabControl tabControl;
    private TabPage tabLog;
    private TabPage tabSendText;
    private TextBox txtLog;
    private TextBox txtSendText;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        this.lblWinKeyerPort = new Label();
        this.cboWinKeyerPort = new ComboBox();
        this.lblTransport = new Label();
        this.cboTransport = new ComboBox();
        this.lblServerAddress = new Label();
        this.txtServerAddress = new TextBox();
        this.lblServerPort = new Label();
        this.nudServerPort = new NumericUpDown();
        this.lblPairingToken = new Label();
        this.txtPairingToken = new TextBox();
        this.lblRelayStatus = new Label();
        this.btnStart = new Button();
        this.btnStop = new Button();
        this.tabControl = new TabControl();
        this.tabLog = new TabPage();
        this.tabSendText = new TabPage();
        this.txtLog = new TextBox();
        this.txtSendText = new TextBox();

        this.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)this.nudServerPort).BeginInit();
        this.tabControl.SuspendLayout();
        this.tabLog.SuspendLayout();
        this.tabSendText.SuspendLayout();

        // lblWinKeyerPort
        this.lblWinKeyerPort.AutoSize = true;
        this.lblWinKeyerPort.Location = new Point(12, 15);
        this.lblWinKeyerPort.Text = "WinKeyer COM Port:";

        // cboWinKeyerPort
        this.cboWinKeyerPort.DropDownStyle = ComboBoxStyle.DropDownList;
        this.cboWinKeyerPort.Location = new Point(12, 33);
        this.cboWinKeyerPort.Size = new Size(100, 23);

        // lblTransport
        this.lblTransport.AutoSize = true;
        this.lblTransport.Location = new Point(130, 15);
        this.lblTransport.Text = "Transport:";

        // cboTransport
        this.cboTransport.DropDownStyle = ComboBoxStyle.DropDownList;
        this.cboTransport.Location = new Point(130, 33);
        this.cboTransport.Size = new Size(120, 23);
        this.cboTransport.Items.AddRange(new object[] { "UDP Direct", "Cloud Relay" });
        this.cboTransport.SelectedIndex = 0;
        this.cboTransport.SelectedIndexChanged += new EventHandler(this.CboTransport_SelectedIndexChanged);

        // lblServerAddress
        this.lblServerAddress.AutoSize = true;
        this.lblServerAddress.Location = new Point(12, 64);
        this.lblServerAddress.Text = "WKR Server IP:";

        // txtServerAddress
        this.txtServerAddress.Location = new Point(12, 82);
        this.txtServerAddress.Size = new Size(120, 23);
        this.txtServerAddress.Text = "127.0.0.1";

        // lblServerPort
        this.lblServerPort.AutoSize = true;
        this.lblServerPort.Location = new Point(140, 64);
        this.lblServerPort.Text = "Port:";

        // nudServerPort
        this.nudServerPort.Location = new Point(140, 82);
        this.nudServerPort.Size = new Size(70, 23);
        this.nudServerPort.Minimum = 1;
        this.nudServerPort.Maximum = 65535;
        this.nudServerPort.Value = 7388;

        // lblPairingToken
        this.lblPairingToken.AutoSize = true;
        this.lblPairingToken.Location = new Point(12, 64);
        this.lblPairingToken.Text = "Pairing Token (from server):";
        this.lblPairingToken.Visible = false;

        // txtPairingToken
        this.txtPairingToken.Location = new Point(12, 82);
        this.txtPairingToken.Size = new Size(420, 23);
        this.txtPairingToken.Font = new Font("Consolas", 8.5F);
        this.txtPairingToken.Visible = false;

        // lblRelayStatus
        this.lblRelayStatus.AutoSize = true;
        this.lblRelayStatus.Location = new Point(270, 36);
        this.lblRelayStatus.Text = "";
        this.lblRelayStatus.ForeColor = System.Drawing.Color.Gray;

        // btnStart
        this.btnStart.Location = new Point(12, 112);
        this.btnStart.Size = new Size(80, 28);
        this.btnStart.Text = "Start";
        this.btnStart.Click += new EventHandler(this.BtnStart_Click);

        // btnStop
        this.btnStop.Location = new Point(12, 112);
        this.btnStop.Size = new Size(80, 28);
        this.btnStop.Text = "Stop";
        this.btnStop.Visible = false;
        this.btnStop.Click += new EventHandler(this.BtnStop_Click);

        // tabControl
        this.tabControl.Location = new Point(12, 148);
        this.tabControl.Size = new Size(460, 275);
        this.tabControl.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        this.tabControl.TabPages.Add(this.tabLog);
        this.tabControl.TabPages.Add(this.tabSendText);

        // tabLog
        this.tabLog.Text = "Log";
        this.tabLog.Padding = new Padding(3);
        this.tabLog.Controls.Add(this.txtLog);

        // tabSendText
        this.tabSendText.Text = "Send Text";
        this.tabSendText.Padding = new Padding(3);
        this.tabSendText.Controls.Add(this.txtSendText);

        // txtLog
        this.txtLog.Dock = DockStyle.Fill;
        this.txtLog.Multiline = true;
        this.txtLog.ReadOnly = true;
        this.txtLog.ScrollBars = ScrollBars.Vertical;
        this.txtLog.Font = new Font("Consolas", 9F);

        // txtSendText
        this.txtSendText.Dock = DockStyle.Fill;
        this.txtSendText.Multiline = true;
        this.txtSendText.WordWrap = true;
        this.txtSendText.ScrollBars = ScrollBars.Vertical;
        this.txtSendText.Font = new Font("Consolas", 11F);

        // MainForm
        this.AutoScaleDimensions = new SizeF(7F, 15F);
        this.AutoScaleMode = AutoScaleMode.Font;
        this.ClientSize = new Size(484, 431);
        this.Controls.Add(this.lblWinKeyerPort);
        this.Controls.Add(this.cboWinKeyerPort);
        this.Controls.Add(this.lblTransport);
        this.Controls.Add(this.cboTransport);
        this.Controls.Add(this.lblServerAddress);
        this.Controls.Add(this.txtServerAddress);
        this.Controls.Add(this.lblServerPort);
        this.Controls.Add(this.nudServerPort);
        this.Controls.Add(this.lblPairingToken);
        this.Controls.Add(this.txtPairingToken);
        this.Controls.Add(this.lblRelayStatus);
        this.Controls.Add(this.tabControl);
        this.Controls.Add(this.btnStart);
        this.Controls.Add(this.btnStop);
        this.MinimumSize = new Size(450, 400);
        this.Name = "MainForm";
        this.Text = "WinKey Remote Client by W1VE";
        this.StartPosition = FormStartPosition.CenterScreen;

        ((System.ComponentModel.ISupportInitialize)this.nudServerPort).EndInit();
        this.tabLog.ResumeLayout(false);
        this.tabLog.PerformLayout();
        this.tabSendText.ResumeLayout(false);
        this.tabSendText.PerformLayout();
        this.tabControl.ResumeLayout(false);
        this.ResumeLayout(false);
        this.PerformLayout();
    }
}
