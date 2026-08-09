namespace WinKeyerEmulator.App;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    // Top config panel controls
    private Label lblKeyingPort;
    private ComboBox cboKeyingPort;
    private RadioButton rdoDTR;
    private RadioButton rdoRTS;
    private Label lblCommandPort;
    private ComboBox cboCommandPort;

    // Transport mode
    private Label lblTransport;
    private ComboBox cboTransport;

    // UDP controls
    private Label lblUdpAddress;
    private TextBox txtUdpAddress;
    private Label lblUdpPort;
    private NumericUpDown nudUdpPort;

    // Cloud Relay controls
    private Label lblPairingToken;
    private TextBox txtPairingToken;
    private Button btnGenerateToken;
    private Button btnCopyToken;
    private Label lblRelayStatus;

    // Middle controls
    private Button btnStart;
    private Button btnStop;
    private CheckBox chkLogRawData;

    // Bottom log
    private TextBox txtLog;

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
        this.lblKeyingPort = new Label();
        this.cboKeyingPort = new ComboBox();
        this.rdoDTR = new RadioButton();
        this.rdoRTS = new RadioButton();
        this.lblCommandPort = new Label();
        this.cboCommandPort = new ComboBox();
        this.lblTransport = new Label();
        this.cboTransport = new ComboBox();
        this.lblUdpAddress = new Label();
        this.txtUdpAddress = new TextBox();
        this.lblUdpPort = new Label();
        this.nudUdpPort = new NumericUpDown();
        this.lblPairingToken = new Label();
        this.txtPairingToken = new TextBox();
        this.btnGenerateToken = new Button();
        this.btnCopyToken = new Button();
        this.lblRelayStatus = new Label();
        this.btnStart = new Button();
        this.btnStop = new Button();
        this.chkLogRawData = new CheckBox();
        this.txtLog = new TextBox();

        this.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)this.nudUdpPort).BeginInit();

        // lblKeyingPort
        this.lblKeyingPort.AutoSize = true;
        this.lblKeyingPort.Location = new Point(12, 15);
        this.lblKeyingPort.Text = "Keying Port:";

        // cboKeyingPort
        this.cboKeyingPort.DropDownStyle = ComboBoxStyle.DropDownList;
        this.cboKeyingPort.Location = new Point(12, 32);
        this.cboKeyingPort.Size = new Size(100, 23);

        // rdoDTR
        this.rdoDTR.AutoSize = true;
        this.rdoDTR.Location = new Point(120, 34);
        this.rdoDTR.Text = "DTR";
        this.rdoDTR.Checked = true;

        // rdoRTS
        this.rdoRTS.AutoSize = true;
        this.rdoRTS.Location = new Point(175, 34);
        this.rdoRTS.Text = "RTS";

        // lblCommandPort
        this.lblCommandPort.AutoSize = true;
        this.lblCommandPort.Location = new Point(12, 62);
        this.lblCommandPort.Text = "Local WinKey Control Port:";

        // cboCommandPort
        this.cboCommandPort.DropDownStyle = ComboBoxStyle.DropDownList;
        this.cboCommandPort.Location = new Point(12, 79);
        this.cboCommandPort.Size = new Size(100, 23);

        // lblTransport
        this.lblTransport.AutoSize = true;
        this.lblTransport.Location = new Point(180, 62);
        this.lblTransport.Text = "Remote Transport:";

        // cboTransport
        this.cboTransport.DropDownStyle = ComboBoxStyle.DropDownList;
        this.cboTransport.Location = new Point(180, 79);
        this.cboTransport.Size = new Size(120, 23);
        this.cboTransport.Items.AddRange(new object[] { "UDP Direct", "Cloud Relay" });
        this.cboTransport.SelectedIndex = 0;
        this.cboTransport.SelectedIndexChanged += new EventHandler(this.CboTransport_SelectedIndexChanged);

        // lblUdpAddress
        this.lblUdpAddress.AutoSize = true;
        this.lblUdpAddress.Location = new Point(12, 110);
        this.lblUdpAddress.Text = "WinKey UDP IP:";

        // txtUdpAddress
        this.txtUdpAddress.Location = new Point(12, 127);
        this.txtUdpAddress.Size = new Size(110, 23);
        this.txtUdpAddress.Text = "127.0.0.1";

        // lblUdpPort
        this.lblUdpPort.AutoSize = true;
        this.lblUdpPort.Location = new Point(130, 110);
        this.lblUdpPort.Text = "Port:";

        // nudUdpPort
        this.nudUdpPort.Location = new Point(130, 127);
        this.nudUdpPort.Size = new Size(70, 23);
        this.nudUdpPort.Minimum = 1;
        this.nudUdpPort.Maximum = 65535;
        this.nudUdpPort.Value = 7388;

        // lblPairingToken
        this.lblPairingToken.AutoSize = true;
        this.lblPairingToken.Location = new Point(12, 110);
        this.lblPairingToken.Text = "Pairing Token:";
        this.lblPairingToken.Visible = false;

        // txtPairingToken
        this.txtPairingToken.Location = new Point(12, 127);
        this.txtPairingToken.Size = new Size(420, 23);
        this.txtPairingToken.Font = new Font("Consolas", 8.5F);
        this.txtPairingToken.Visible = false;

        // btnGenerateToken
        this.btnGenerateToken.Location = new Point(440, 126);
        this.btnGenerateToken.Size = new Size(75, 25);
        this.btnGenerateToken.Text = "Generate";
        this.btnGenerateToken.Visible = false;
        this.btnGenerateToken.Click += new EventHandler(this.BtnGenerateToken_Click);

        // btnCopyToken
        this.btnCopyToken.Location = new Point(520, 126);
        this.btnCopyToken.Size = new Size(50, 25);
        this.btnCopyToken.Text = "Copy";
        this.btnCopyToken.Visible = false;
        this.btnCopyToken.Enabled = false;
        this.btnCopyToken.Click += new EventHandler(this.BtnCopyToken_Click);

        // lblRelayStatus
        this.lblRelayStatus.AutoSize = true;
        this.lblRelayStatus.Location = new Point(320, 82);
        this.lblRelayStatus.Text = "";
        this.lblRelayStatus.ForeColor = System.Drawing.Color.Gray;

        // btnStart
        this.btnStart.Location = new Point(12, 160);
        this.btnStart.Size = new Size(100, 30);
        this.btnStart.Text = "Start";
        this.btnStart.Click += new EventHandler(this.BtnStart_Click);

        // btnStop
        this.btnStop.Location = new Point(12, 160);
        this.btnStop.Size = new Size(100, 30);
        this.btnStop.Text = "Stop";
        this.btnStop.Visible = false;
        this.btnStop.Click += new EventHandler(this.BtnStop_Click);

        // chkLogRawData
        this.chkLogRawData.AutoSize = true;
        this.chkLogRawData.Location = new Point(130, 167);
        this.chkLogRawData.Text = "Log raw data";
        this.chkLogRawData.CheckedChanged += new EventHandler(this.ChkLogRawData_CheckedChanged);

        // txtLog
        this.txtLog.Location = new Point(12, 200);
        this.txtLog.Multiline = true;
        this.txtLog.ReadOnly = true;
        this.txtLog.ScrollBars = ScrollBars.Vertical;
        this.txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        this.txtLog.Size = new Size(560, 230);
        this.txtLog.Font = new Font("Consolas", 9F);

        // MainForm
        this.AutoScaleDimensions = new SizeF(7F, 15F);
        this.AutoScaleMode = AutoScaleMode.Font;
        this.ClientSize = new Size(584, 441);
        this.Controls.Add(this.lblKeyingPort);
        this.Controls.Add(this.cboKeyingPort);
        this.Controls.Add(this.rdoDTR);
        this.Controls.Add(this.rdoRTS);
        this.Controls.Add(this.lblCommandPort);
        this.Controls.Add(this.cboCommandPort);
        this.Controls.Add(this.lblTransport);
        this.Controls.Add(this.cboTransport);
        this.Controls.Add(this.lblUdpAddress);
        this.Controls.Add(this.txtUdpAddress);
        this.Controls.Add(this.lblUdpPort);
        this.Controls.Add(this.nudUdpPort);
        this.Controls.Add(this.lblPairingToken);
        this.Controls.Add(this.txtPairingToken);
        this.Controls.Add(this.btnGenerateToken);
        this.Controls.Add(this.btnCopyToken);
        this.Controls.Add(this.lblRelayStatus);
        this.Controls.Add(this.btnStart);
        this.Controls.Add(this.btnStop);
        this.Controls.Add(this.chkLogRawData);
        this.Controls.Add(this.txtLog);
        this.MinimumSize = new Size(500, 400);
        this.Name = "MainForm";
        this.Text = "WinKey Remote Server by W1VE";
        this.StartPosition = FormStartPosition.CenterScreen;

        ((System.ComponentModel.ISupportInitialize)this.nudUdpPort).EndInit();
        this.ResumeLayout(false);
        this.PerformLayout();
    }
}
