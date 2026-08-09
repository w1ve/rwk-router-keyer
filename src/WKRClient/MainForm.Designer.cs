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

    // Paddle settings
    private Label lblKeyMode;
    private ComboBox cboKeyMode;
    private CheckBox chkPaddleSwap;
    private CheckBox chkAutospace;

    private Button btnStart;
    private Button btnStop;

    // TabControl
    private TabControl tabControl;
    private TabPage tabLog;
    private TabPage tabSendText;
    private TabPage tabSoftKeyer;
    private TextBox txtLog;
    private TextBox txtSendText;

    // Soft Keyer tab controls
    private Button btnDit;
    private Button btnDah;
    private Label lblSoftSpeed;
    private NumericUpDown nudSoftSpeed;
    private Label lblSoftKeyMode;
    private ComboBox cboSoftKeyMode;
    private TextBox txtSoftKeyerOutput;
    private Label lblSoftKeyerStatus;
    private CheckBox chkSoftKeyerEnabled;

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
        this.lblKeyMode = new Label();
        this.cboKeyMode = new ComboBox();
        this.chkPaddleSwap = new CheckBox();
        this.chkAutospace = new CheckBox();
        this.btnStart = new Button();
        this.btnStop = new Button();
        this.tabControl = new TabControl();
        this.tabLog = new TabPage();
        this.tabSendText = new TabPage();
        this.tabSoftKeyer = new TabPage();
        this.txtLog = new TextBox();
        this.txtSendText = new TextBox();
        this.btnDit = new Button();
        this.btnDah = new Button();
        this.lblSoftSpeed = new Label();
        this.nudSoftSpeed = new NumericUpDown();
        this.lblSoftKeyMode = new Label();
        this.cboSoftKeyMode = new ComboBox();
        this.txtSoftKeyerOutput = new TextBox();
        this.lblSoftKeyerStatus = new Label();
        this.chkSoftKeyerEnabled = new CheckBox();

        this.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)this.nudServerPort).BeginInit();
        ((System.ComponentModel.ISupportInitialize)this.nudSoftSpeed).BeginInit();
        this.tabControl.SuspendLayout();
        this.tabLog.SuspendLayout();
        this.tabSendText.SuspendLayout();
        this.tabSoftKeyer.SuspendLayout();

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

        // lblKeyMode
        this.lblKeyMode.AutoSize = true;
        this.lblKeyMode.Location = new Point(270, 15);
        this.lblKeyMode.Text = "Key Mode:";

        // cboKeyMode
        this.cboKeyMode.DropDownStyle = ComboBoxStyle.DropDownList;
        this.cboKeyMode.Location = new Point(270, 33);
        this.cboKeyMode.Size = new Size(90, 23);
        this.cboKeyMode.Items.AddRange(new object[] { "Iambic B", "Iambic A", "Ultimatic", "Bug" });
        this.cboKeyMode.SelectedIndex = 0;

        // chkPaddleSwap
        this.chkPaddleSwap.AutoSize = true;
        this.chkPaddleSwap.Location = new Point(370, 20);
        this.chkPaddleSwap.Text = "Swap";

        // chkAutospace
        this.chkAutospace.AutoSize = true;
        this.chkAutospace.Location = new Point(370, 40);
        this.chkAutospace.Text = "Autospace";

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
        this.tabControl.TabPages.Add(this.tabSoftKeyer);

        // tabLog
        this.tabLog.Text = "Log";
        this.tabLog.Padding = new Padding(3);
        this.tabLog.Controls.Add(this.txtLog);

        // tabSendText
        this.tabSendText.Text = "Send Text";
        this.tabSendText.Padding = new Padding(3);
        this.tabSendText.Controls.Add(this.txtSendText);

        // tabSoftKeyer
        this.tabSoftKeyer.Text = "Soft Keyer";
        this.tabSoftKeyer.Padding = new Padding(3);
        this.tabSoftKeyer.Controls.Add(this.chkSoftKeyerEnabled);
        this.tabSoftKeyer.Controls.Add(this.lblSoftSpeed);
        this.tabSoftKeyer.Controls.Add(this.nudSoftSpeed);
        this.tabSoftKeyer.Controls.Add(this.lblSoftKeyMode);
        this.tabSoftKeyer.Controls.Add(this.cboSoftKeyMode);
        this.tabSoftKeyer.Controls.Add(this.btnDit);
        this.tabSoftKeyer.Controls.Add(this.btnDah);
        this.tabSoftKeyer.Controls.Add(this.txtSoftKeyerOutput);
        this.tabSoftKeyer.Controls.Add(this.lblSoftKeyerStatus);

        // chkSoftKeyerEnabled
        this.chkSoftKeyerEnabled.AutoSize = true;
        this.chkSoftKeyerEnabled.Location = new Point(10, 12);
        this.chkSoftKeyerEnabled.Text = "Enable Soft Keyer (no WinKeyer needed)";
        this.chkSoftKeyerEnabled.CheckedChanged += new EventHandler(this.ChkSoftKeyerEnabled_CheckedChanged);

        // lblSoftSpeed
        this.lblSoftSpeed.AutoSize = true;
        this.lblSoftSpeed.Location = new Point(10, 42);
        this.lblSoftSpeed.Text = "Speed:";

        // nudSoftSpeed
        this.nudSoftSpeed.Location = new Point(60, 40);
        this.nudSoftSpeed.Size = new Size(60, 23);
        this.nudSoftSpeed.Minimum = 5;
        this.nudSoftSpeed.Maximum = 60;
        this.nudSoftSpeed.Value = 25;
        this.nudSoftSpeed.ValueChanged += new EventHandler(this.NudSoftSpeed_ValueChanged);

        // lblSoftKeyMode
        this.lblSoftKeyMode.AutoSize = true;
        this.lblSoftKeyMode.Location = new Point(135, 42);
        this.lblSoftKeyMode.Text = "Mode:";

        // cboSoftKeyMode
        this.cboSoftKeyMode.DropDownStyle = ComboBoxStyle.DropDownList;
        this.cboSoftKeyMode.Location = new Point(180, 40);
        this.cboSoftKeyMode.Size = new Size(90, 23);
        this.cboSoftKeyMode.Items.AddRange(new object[] { "Iambic B", "Iambic A", "Ultimatic", "Bug" });
        this.cboSoftKeyMode.SelectedIndex = 0;
        this.cboSoftKeyMode.SelectedIndexChanged += new EventHandler(this.CboSoftKeyMode_SelectedIndexChanged);

        // lblSoftKeyerStatus
        this.lblSoftKeyerStatus.AutoSize = true;
        this.lblSoftKeyerStatus.Location = new Point(290, 42);
        this.lblSoftKeyerStatus.Text = "";
        this.lblSoftKeyerStatus.ForeColor = System.Drawing.Color.Gray;

        // btnDit
        this.btnDit.Location = new Point(10, 75);
        this.btnDit.Size = new Size(100, 60);
        this.btnDit.Text = "DIT\n(, or LMB)";
        this.btnDit.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
        this.btnDit.BackColor = System.Drawing.Color.LightGray;
        this.btnDit.FlatStyle = FlatStyle.Flat;
        this.btnDit.MouseDown += new MouseEventHandler(this.BtnDit_MouseDown);
        this.btnDit.MouseUp += new MouseEventHandler(this.BtnDit_MouseUp);

        // btnDah
        this.btnDah.Location = new Point(120, 75);
        this.btnDah.Size = new Size(100, 60);
        this.btnDah.Text = "DAH\n(. or RMB)";
        this.btnDah.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
        this.btnDah.BackColor = System.Drawing.Color.LightGray;
        this.btnDah.FlatStyle = FlatStyle.Flat;
        this.btnDah.MouseDown += new MouseEventHandler(this.BtnDah_MouseDown);
        this.btnDah.MouseUp += new MouseEventHandler(this.BtnDah_MouseUp);

        // txtSoftKeyerOutput
        this.txtSoftKeyerOutput.Location = new Point(10, 145);
        this.txtSoftKeyerOutput.Size = new Size(430, 90);
        this.txtSoftKeyerOutput.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        this.txtSoftKeyerOutput.Multiline = true;
        this.txtSoftKeyerOutput.ReadOnly = true;
        this.txtSoftKeyerOutput.ScrollBars = ScrollBars.Vertical;
        this.txtSoftKeyerOutput.Font = new Font("Consolas", 11F);

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
        this.Controls.Add(this.lblKeyMode);
        this.Controls.Add(this.cboKeyMode);
        this.Controls.Add(this.chkPaddleSwap);
        this.Controls.Add(this.chkAutospace);
        this.Controls.Add(this.tabControl);
        this.Controls.Add(this.btnStart);
        this.Controls.Add(this.btnStop);
        this.MinimumSize = new Size(500, 400);
        this.Name = "MainForm";
        this.Text = "WinKey Remote Client by W1VE";
        this.StartPosition = FormStartPosition.CenterScreen;

        ((System.ComponentModel.ISupportInitialize)this.nudServerPort).EndInit();
        ((System.ComponentModel.ISupportInitialize)this.nudSoftSpeed).EndInit();
        this.tabLog.ResumeLayout(false);
        this.tabLog.PerformLayout();
        this.tabSendText.ResumeLayout(false);
        this.tabSendText.PerformLayout();
        this.tabSoftKeyer.ResumeLayout(false);
        this.tabSoftKeyer.PerformLayout();
        this.tabControl.ResumeLayout(false);
        this.ResumeLayout(false);
        this.PerformLayout();
    }
}
