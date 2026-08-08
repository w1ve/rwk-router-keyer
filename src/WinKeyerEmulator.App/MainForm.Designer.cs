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
    private Label lblUdpAddress;
    private TextBox txtUdpAddress;
    private Label lblUdpPort;
    private NumericUpDown nudUdpPort;

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
        this.lblUdpAddress = new Label();
        this.txtUdpAddress = new TextBox();
        this.lblUdpPort = new Label();
        this.nudUdpPort = new NumericUpDown();
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
        this.lblCommandPort.Location = new Point(12, 68);
        this.lblCommandPort.Text = "Local WinKey Control Port:";

        // cboCommandPort
        this.cboCommandPort.DropDownStyle = ComboBoxStyle.DropDownList;
        this.cboCommandPort.Location = new Point(12, 86);
        this.cboCommandPort.Size = new Size(100, 23);

        // lblUdpAddress
        this.lblUdpAddress.AutoSize = true;
        this.lblUdpAddress.Location = new Point(180, 68);
        this.lblUdpAddress.Text = "WinKey UDP IP:";

        // txtUdpAddress
        this.txtUdpAddress.Location = new Point(180, 86);
        this.txtUdpAddress.Size = new Size(110, 23);
        this.txtUdpAddress.Text = "127.0.0.1";

        // lblUdpPort
        this.lblUdpPort.AutoSize = true;
        this.lblUdpPort.Location = new Point(300, 68);
        this.lblUdpPort.Text = "Port:";

        // nudUdpPort
        this.nudUdpPort.Location = new Point(300, 86);
        this.nudUdpPort.Size = new Size(70, 23);
        this.nudUdpPort.Minimum = 1;
        this.nudUdpPort.Maximum = 65535;
        this.nudUdpPort.Value = 7388;

        // btnStart
        this.btnStart.Location = new Point(12, 120);
        this.btnStart.Size = new Size(100, 30);
        this.btnStart.Text = "Start";
        this.btnStart.Click += new EventHandler(this.BtnStart_Click);

        // btnStop
        this.btnStop.Location = new Point(12, 120);
        this.btnStop.Size = new Size(100, 30);
        this.btnStop.Text = "Stop";
        this.btnStop.Visible = false;
        this.btnStop.Click += new EventHandler(this.BtnStop_Click);

        // chkLogRawData
        this.chkLogRawData.AutoSize = true;
        this.chkLogRawData.Location = new Point(130, 127);
        this.chkLogRawData.Text = "Log raw data";
        this.chkLogRawData.CheckedChanged += new EventHandler(this.ChkLogRawData_CheckedChanged);

        // txtLog
        this.txtLog.Location = new Point(12, 160);
        this.txtLog.Multiline = true;
        this.txtLog.ReadOnly = true;
        this.txtLog.ScrollBars = ScrollBars.Vertical;
        this.txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        this.txtLog.Size = new Size(560, 240);
        this.txtLog.Font = new Font("Consolas", 9F);

        // MainForm
        this.AutoScaleDimensions = new SizeF(7F, 15F);
        this.AutoScaleMode = AutoScaleMode.Font;
        this.ClientSize = new Size(584, 411);
        this.Controls.Add(this.lblKeyingPort);
        this.Controls.Add(this.cboKeyingPort);
        this.Controls.Add(this.rdoDTR);
        this.Controls.Add(this.rdoRTS);
        this.Controls.Add(this.lblCommandPort);
        this.Controls.Add(this.cboCommandPort);
        this.Controls.Add(this.lblUdpAddress);
        this.Controls.Add(this.txtUdpAddress);
        this.Controls.Add(this.lblUdpPort);
        this.Controls.Add(this.nudUdpPort);
        this.Controls.Add(this.btnStart);
        this.Controls.Add(this.btnStop);
        this.Controls.Add(this.chkLogRawData);
        this.Controls.Add(this.txtLog);
        this.MinimumSize = new Size(500, 350);
        this.Name = "MainForm";
        this.Text = "WinKey Remote Server by W1VE";
        this.StartPosition = FormStartPosition.CenterScreen;

        ((System.ComponentModel.ISupportInitialize)this.nudUdpPort).EndInit();
        this.ResumeLayout(false);
        this.PerformLayout();
    }
}
