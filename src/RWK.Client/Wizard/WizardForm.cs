/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using RWK.Shared.Config;

namespace RWK.Client.Wizard;

/// <summary>
/// The Port Forward Wizard form — a 5-step dialog that walks the operator through
/// selecting a radio/service, configuring the endpoint, choosing extras, and applying
/// the generated rules to the Client's forwarding table.
/// </summary>
/// <remarks>
/// After Apply, the form asks "Add another radio or device?" — if yes it resets to Step 1.
/// The form exposes <see cref="GeneratedRules"/> for the caller (MainForm) to merge into
/// the live rule set.
/// </remarks>
public sealed class WizardForm : Form
{
    // ──────────────────────────────────────────────────────────────────────────────
    //  State
    // ──────────────────────────────────────────────────────────────────────────────

    private readonly RadioCatalog _catalog;
    private readonly IReadOnlyList<ForwardRule> _existingRules;
    private readonly List<WizardProfile> _appliedProfiles = new();

    private CatalogEntry? _selectedEntry;
    private string _stationTarget = "";
    private string _profileName = "";
    private bool _enableRules;
    private List<CatalogEntry> _selectedExtras = new();

    private int _currentStep;
    private const int TotalSteps = 5;

    // ──────────────────────────────────────────────────────────────────────────────
    //  Controls
    // ──────────────────────────────────────────────────────────────────────────────

    private Panel _stepPanel = null!;
    private Label _stepTitle = null!;
    private Label _stepDescription = null!;
    private Button _backButton = null!;
    private Button _nextButton = null!;
    private Button _cancelButton = null!;
    private Label _stepIndicator = null!;

    // Step 1: Radio selection
    private ListBox _radioList = null!;
    private TextBox _searchBox = null!;

    // Step 2: (skipped for now — single control path per catalog entry)

    // Step 3: Endpoint location
    private RadioButton _endpointLan = null!;
    private RadioButton _endpointStationPc = null!;
    private TextBox _stationTargetBox = null!;

    // Step 4: Extras
    private CheckedListBox _extrasList = null!;

    // Step 5: Review
    private DataGridView _reviewGrid = null!;
    private CheckBox _enableImmediately = null!;
    private TextBox _profileNameBox = null!;

    /// <summary>
    /// All profiles that were applied during this wizard session (may be multiple
    /// if the user chose "Add another").
    /// </summary>
    public IReadOnlyList<WizardProfile> AppliedProfiles => _appliedProfiles;

    /// <summary>
    /// The aggregated list of rules from all applied profiles.
    /// The caller merges these into the live rule set.
    /// </summary>
    public List<ProfileForwardRule> GeneratedRules { get; } = new();

    // ──────────────────────────────────────────────────────────────────────────────
    //  Construction
    // ──────────────────────────────────────────────────────────────────────────────

    public WizardForm(RadioCatalog catalog, IReadOnlyList<ForwardRule> existingRules)
    {
        _catalog = catalog;
        _existingRules = existingRules;
        InitializeWizardLayout();
        ShowStep(1);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Layout
    // ──────────────────────────────────────────────────────────────────────────────

    private void InitializeWizardLayout()
    {
        Text = "RWK Port Forward Wizard";
        Size = new Size(620, 500);
        MinimumSize = new Size(580, 460);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        // Step indicator at top
        _stepIndicator = new Label
        {
            Text = "Step 1 of 5",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = SystemColors.GrayText,
            Location = new Point(12, 8),
            AutoSize = true
        };
        Controls.Add(_stepIndicator);

        // Step title
        _stepTitle = new Label
        {
            Text = "Select Radio or Device",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            Location = new Point(12, 28),
            AutoSize = true
        };
        Controls.Add(_stepTitle);

        // Step description
        _stepDescription = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 9f),
            Location = new Point(12, 56),
            Size = new Size(580, 36),
            AutoSize = false
        };
        Controls.Add(_stepDescription);

        // Main step panel (holds step-specific controls)
        _stepPanel = new Panel
        {
            Location = new Point(12, 96),
            Size = new Size(580, 310),
            BorderStyle = BorderStyle.None
        };
        Controls.Add(_stepPanel);

        // Navigation buttons
        _backButton = new Button
        {
            Text = "< Back",
            Size = new Size(80, 30),
            Location = new Point(330, 420),
            UseVisualStyleBackColor = true,
            Enabled = false
        };
        _backButton.Click += OnBackClick;
        Controls.Add(_backButton);

        _nextButton = new Button
        {
            Text = "Next >",
            Size = new Size(80, 30),
            Location = new Point(416, 420),
            UseVisualStyleBackColor = true
        };
        _nextButton.Click += OnNextClick;
        Controls.Add(_nextButton);

        _cancelButton = new Button
        {
            Text = "Cancel",
            Size = new Size(80, 30),
            Location = new Point(506, 420),
            UseVisualStyleBackColor = true
        };
        _cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(_cancelButton);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Step Navigation
    // ──────────────────────────────────────────────────────────────────────────────

    private void ShowStep(int step)
    {
        _currentStep = step;
        _stepPanel.Controls.Clear();
        _stepIndicator.Text = $"Step {step} of {TotalSteps}";
        _backButton.Enabled = step > 1;
        _nextButton.Text = step == TotalSteps ? "Apply" : "Next >";

        switch (step)
        {
            case 1: BuildStep1_Radio(); break;
            case 2: BuildStep2_ControlPath(); break;
            case 3: BuildStep3_Endpoint(); break;
            case 4: BuildStep4_Extras(); break;
            case 5: BuildStep5_Review(); break;
        }
    }

    private void OnBackClick(object? sender, EventArgs e)
    {
        if (_currentStep > 1)
            ShowStep(_currentStep - 1);
    }

    private void OnNextClick(object? sender, EventArgs e)
    {
        if (!ValidateCurrentStep())
            return;

        if (_currentStep < TotalSteps)
        {
            ShowStep(_currentStep + 1);
        }
        else
        {
            ApplyWizard();
        }
    }

    private bool ValidateCurrentStep()
    {
        switch (_currentStep)
        {
            case 1:
                if (_radioList.SelectedItem is not CatalogEntry entry)
                {
                    MessageBox.Show("Please select a radio or device.", "Selection Required",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
                _selectedEntry = entry;
                return true;

            case 2:
                // Currently single path per entry — always valid.
                return true;

            case 3:
                if (_selectedEntry?.IsGenericSerial == true)
                {
                    // Validate serial bridge fields
                    if (string.IsNullOrWhiteSpace(_deviceNameBox?.Text))
                    {
                        MessageBox.Show("Please enter a device name.", "Name Required",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return false;
                    }
                    if (string.IsNullOrWhiteSpace(_stationComPortBox?.Text))
                    {
                        MessageBox.Show("Please enter the Station COM port.", "Port Required",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return false;
                    }
                    if (!int.TryParse(_tcpPortBox?.Text, out int tcpPort) || tcpPort < 1 || tcpPort > 65535)
                    {
                        MessageBox.Show("TCP port must be 1-65535.", "Invalid Port",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return false;
                    }
                    if (!int.TryParse(_clientComPortBox?.Text, out int comNum) || comNum < 1 || comNum > 256)
                    {
                        MessageBox.Show("Client COM port number must be 1-256.", "Invalid Port",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return false;
                    }
                    _stationTarget = "127.0.0.1"; // Serial bridge always targets Station localhost
                    return true;
                }

                _stationTarget = _stationTargetBox.Text.Trim();
                if (string.IsNullOrEmpty(_stationTarget))
                {
                    MessageBox.Show("Please enter the station target address.", "Address Required",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
                return true;

            case 4:
                _selectedExtras.Clear();
                foreach (var item in _extrasList.CheckedItems)
                {
                    if (item is CatalogEntry extra)
                        _selectedExtras.Add(extra);
                }
                return true;

            case 5:
                _profileName = _profileNameBox.Text.Trim();
                _enableRules = _enableImmediately.Checked;
                if (string.IsNullOrEmpty(_profileName))
                {
                    MessageBox.Show("Please enter a profile name.", "Name Required",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
                return true;
        }
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Step 1: Radio Selection
    // ──────────────────────────────────────────────────────────────────────────────

    private void BuildStep1_Radio()
    {
        _stepTitle.Text = "Select Radio or Device";
        _stepDescription.Text = "Choose your radio, interface, or service from the list below. " +
            "Generic options appear at the bottom for unlisted devices.";

        _searchBox = new TextBox
        {
            Location = new Point(0, 0),
            Size = new Size(280, 24),
            PlaceholderText = "Search..."
        };
        _searchBox.TextChanged += OnSearchChanged;
        _stepPanel.Controls.Add(_searchBox);

        _radioList = new ListBox
        {
            Location = new Point(0, 30),
            Size = new Size(575, 270),
            Font = new Font("Segoe UI", 9.5f)
        };
        _stepPanel.Controls.Add(_radioList);

        PopulateRadioList("");
    }

    private void PopulateRadioList(string filter)
    {
        _radioList.Items.Clear();
        var entries = _catalog.Entries
            .Where(e => !e.IsService) // Services go in Step 4
            .OrderBy(e => e.IsGenericSerial || e.IsGenericService ? 1 : 0)
            .ThenBy(e => e.Vendor)
            .ThenBy(e => e.DisplayName);

        foreach (var entry in entries)
        {
            if (!string.IsNullOrEmpty(filter))
            {
                string searchable = $"{entry.Vendor} {entry.DisplayName} {string.Join(" ", entry.Models)}";
                if (!searchable.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            _radioList.Items.Add(entry);
        }

        if (_selectedEntry is not null && _radioList.Items.Contains(_selectedEntry))
            _radioList.SelectedItem = _selectedEntry;
    }

    private void OnSearchChanged(object? sender, EventArgs e)
    {
        PopulateRadioList(_searchBox.Text);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Step 2: Control Path (currently passthrough — single path per entry)
    // ──────────────────────────────────────────────────────────────────────────────

    private void BuildStep2_ControlPath()
    {
        // For v1.0, each catalog entry IS a control path. Skip directly to Step 3.
        // Show a brief confirmation of what was selected.
        _stepTitle.Text = "Control Path";
        _stepDescription.Text = $"Using: {_selectedEntry?.DisplayName ?? "Unknown"}";

        var infoLabel = new Label
        {
            Text = $"Vendor: {_selectedEntry?.Vendor}\n" +
                   $"Software: {_selectedEntry?.Software ?? "N/A"}\n" +
                   $"Models: {string.Join(", ", _selectedEntry?.Models ?? new())}\n" +
                   $"Confidence: {_selectedEntry?.Confidence ?? "unknown"}",
            Font = new Font("Segoe UI", 9.5f),
            Location = new Point(0, 10),
            Size = new Size(560, 100),
            AutoSize = false
        };
        _stepPanel.Controls.Add(infoLabel);

        if (_selectedEntry?.Confidence == "unverified")
        {
            var bannerBox = new TextBox
            {
                Text = "This entry has not been verified against vendor documentation. " +
                       "Port numbers may be incorrect. Please report back if it works!",
                ForeColor = Color.DarkOrange,
                BackColor = _stepPanel.BackColor,
                Font = new Font("Segoe UI", 9f, FontStyle.Italic),
                Location = new Point(0, 120),
                Size = new Size(575, 50),
                Multiline = true,
                WordWrap = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                TabStop = false
            };
            _stepPanel.Controls.Add(bannerBox);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Step 3: Endpoint Location / Serial Bridge Config
    // ──────────────────────────────────────────────────────────────────────────────

    // Serial bridge state (populated when _selectedEntry.IsGenericSerial)
    private SerialPreset? _selectedPreset;
    private ComboBox? _serialPresetCombo;
    private ComboBox? _baudCombo;
    private ComboBox? _dataBitsCombo;
    private ComboBox? _parityCombo;
    private ComboBox? _stopBitsCombo;
    private ComboBox? _dtrCombo;
    private ComboBox? _rtsCombo;
    private TextBox? _stationComPortBox;
    private TextBox? _clientComPortBox;
    private TextBox? _tcpPortBox;
    private TextBox? _deviceNameBox;

    private void BuildStep3_Endpoint()
    {
        if (_selectedEntry?.IsGenericSerial == true)
        {
            BuildStep3_SerialBridge();
            return;
        }

        _stepTitle.Text = "Station Target Address";

        var prompt = _selectedEntry?.Prompts.GetValueOrDefault("stationTarget");
        _stepDescription.Text = prompt?.Why ?? "Where does the target device live on the Station's network?";

        // Radio buttons for endpoint type
        bool isStationPc = _selectedEntry?.EndpointLocation == "station-pc";

        _endpointStationPc = new RadioButton
        {
            Text = "On the Station PC itself (127.0.0.1)",
            Location = new Point(0, 5),
            AutoSize = true,
            Checked = isStationPc
        };
        _endpointStationPc.CheckedChanged += OnEndpointTypeChanged;
        _stepPanel.Controls.Add(_endpointStationPc);

        _endpointLan = new RadioButton
        {
            Text = "On the Station's LAN (enter IP address below)",
            Location = new Point(0, 30),
            AutoSize = true,
            Checked = !isStationPc
        };
        _endpointLan.CheckedChanged += OnEndpointTypeChanged;
        _stepPanel.Controls.Add(_endpointLan);

        var targetLabel = new Label
        {
            Text = prompt?.Label ?? "Target IP address:",
            Location = new Point(0, 65),
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        _stepPanel.Controls.Add(targetLabel);

        _stationTargetBox = new TextBox
        {
            Location = new Point(0, 88),
            Size = new Size(200, 24),
            Text = isStationPc ? "127.0.0.1" : _stationTarget
        };
        _stepPanel.Controls.Add(_stationTargetBox);

        // How to find it
        if (prompt is not null && !string.IsNullOrEmpty(prompt.HowToFind))
        {
            var howToFindBox = new TextBox
            {
                Text = $"Where to find it: {prompt.HowToFind}",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = SystemColors.GrayText,
                BackColor = _stepPanel.BackColor,
                Location = new Point(0, 120),
                Size = new Size(575, 60),
                Multiline = true,
                WordWrap = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                ScrollBars = ScrollBars.Vertical,
                TabStop = false
            };
            _stepPanel.Controls.Add(howToFindBox);
        }

        // If wrong
        if (prompt is not null && !string.IsNullOrEmpty(prompt.IfWrong))
        {
            var ifWrongBox = new TextBox
            {
                Text = $"If this is wrong: {prompt.IfWrong}",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(180, 80, 0),
                BackColor = _stepPanel.BackColor,
                Location = new Point(0, 188),
                Size = new Size(575, 60),
                Multiline = true,
                WordWrap = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                ScrollBars = ScrollBars.Vertical,
                TabStop = false
            };
            _stepPanel.Controls.Add(ifWrongBox);
        }
    }

    private void BuildStep3_SerialBridge()
    {
        _stepTitle.Text = "Serial Bridge Configuration";
        _stepDescription.Text = "Configure the serial port parameters for this CAT bridge. " +
            "Select your radio type to pre-fill settings, then adjust as needed.";

        int y = 0;

        // Device name
        AddLabel("Device name:", 0, y, true);
        _deviceNameBox = new TextBox { Location = new Point(140, y), Size = new Size(200, 24), Text = "CAT Bridge" };
        _stepPanel.Controls.Add(_deviceNameBox);
        y += 30;

        // Radio type preset
        AddLabel("Radio type:", 0, y, true);
        _serialPresetCombo = new ComboBox
        {
            Location = new Point(140, y), Size = new Size(320, 24),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        foreach (var preset in SerialPresets.All)
            _serialPresetCombo.Items.Add(preset);
        _serialPresetCombo.SelectedIndex = _serialPresetCombo.Items.Count - 1; // Generic last
        _serialPresetCombo.SelectedIndexChanged += OnPresetChanged;
        _stepPanel.Controls.Add(_serialPresetCombo);
        y += 30;

        // Station COM port
        AddLabel("Station COM port:", 0, y, false);
        _stationComPortBox = new TextBox { Location = new Point(140, y), Size = new Size(80, 24), Text = "COM3" };
        _stepPanel.Controls.Add(_stationComPortBox);
        AddLabel("(real port connected to radio)", 230, y, false).ForeColor = SystemColors.GrayText;
        y += 28;

        // Client virtual COM port
        AddLabel("Client COM port:", 0, y, false);
        _clientComPortBox = new TextBox { Location = new Point(140, y), Size = new Size(80, 24), Text = "20" };
        _stepPanel.Controls.Add(_clientComPortBox);
        AddLabel("(virtual port for your logger — COM20+)", 230, y, false).ForeColor = SystemColors.GrayText;
        y += 28;

        // TCP port
        AddLabel("TCP port:", 0, y, false);
        _tcpPortBox = new TextBox { Location = new Point(140, y), Size = new Size(80, 24), Text = "4000" };
        _stepPanel.Controls.Add(_tcpPortBox);
        AddLabel("(tunnel port — increment for additional bridges)", 230, y, false).ForeColor = SystemColors.GrayText;
        y += 32;

        // Serial parameters (editable)
        var paramGroup = new GroupBox
        {
            Text = "Serial Parameters (from preset, editable)",
            Location = new Point(0, y),
            Size = new Size(575, 100)
        };
        _stepPanel.Controls.Add(paramGroup);

        int py = 20;
        AddLabelTo(paramGroup, "Baud:", 10, py);
        _baudCombo = new ComboBox { Location = new Point(90, py), Size = new Size(90, 22), DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (int b in SerialPresets.BaudRates) _baudCombo.Items.Add(b);
        _baudCombo.SelectedItem = 9600;
        paramGroup.Controls.Add(_baudCombo);

        AddLabelTo(paramGroup, "Data:", 190, py);
        _dataBitsCombo = new ComboBox { Location = new Point(230, py), Size = new Size(50, 22), DropDownStyle = ComboBoxStyle.DropDownList };
        _dataBitsCombo.Items.AddRange(new object[] { 7, 8 });
        _dataBitsCombo.SelectedItem = 8;
        paramGroup.Controls.Add(_dataBitsCombo);

        AddLabelTo(paramGroup, "Parity:", 290, py);
        _parityCombo = new ComboBox { Location = new Point(340, py), Size = new Size(80, 22), DropDownStyle = ComboBoxStyle.DropDownList };
        _parityCombo.Items.AddRange(SerialPresets.ParityOptions);
        _parityCombo.SelectedItem = "None";
        paramGroup.Controls.Add(_parityCombo);

        AddLabelTo(paramGroup, "Stop:", 430, py);
        _stopBitsCombo = new ComboBox { Location = new Point(470, py), Size = new Size(50, 22), DropDownStyle = ComboBoxStyle.DropDownList };
        _stopBitsCombo.Items.AddRange(new object[] { 1, 2 });
        _stopBitsCombo.SelectedItem = 1;
        paramGroup.Controls.Add(_stopBitsCombo);

        py += 30;
        AddLabelTo(paramGroup, "DTR:", 10, py);
        _dtrCombo = new ComboBox { Location = new Point(90, py), Size = new Size(100, 22), DropDownStyle = ComboBoxStyle.DropDownList };
        _dtrCombo.Items.AddRange(SerialPresets.HandshakeOptions);
        _dtrCombo.SelectedItem = "Off";
        paramGroup.Controls.Add(_dtrCombo);

        AddLabelTo(paramGroup, "RTS:", 200, py);
        _rtsCombo = new ComboBox { Location = new Point(240, py), Size = new Size(100, 22), DropDownStyle = ComboBoxStyle.DropDownList };
        _rtsCombo.Items.AddRange(SerialPresets.HandshakeOptions);
        _rtsCombo.SelectedItem = "Off";
        paramGroup.Controls.Add(_rtsCombo);

        // Apply the default preset
        OnPresetChanged(null, EventArgs.Empty);
    }

    private void OnPresetChanged(object? sender, EventArgs e)
    {
        if (_serialPresetCombo?.SelectedItem is not SerialPreset preset) return;
        _selectedPreset = preset;

        _baudCombo!.SelectedItem = preset.BaudRate;
        _dataBitsCombo!.SelectedItem = preset.DataBits;
        _parityCombo!.SelectedItem = preset.Parity;
        _stopBitsCombo!.SelectedItem = preset.StopBits;
        _dtrCombo!.SelectedItem = preset.DtrControl;
        _rtsCombo!.SelectedItem = preset.RtsControl;
    }

    private Label AddLabel(string text, int x, int y, bool bold)
    {
        var lbl = new Label
        {
            Text = text,
            Location = new Point(x, y + 3),
            AutoSize = true,
            Font = bold ? new Font("Segoe UI", 9f, FontStyle.Bold) : new Font("Segoe UI", 9f)
        };
        _stepPanel.Controls.Add(lbl);
        return lbl;
    }

    private static Label AddLabelTo(Control parent, string text, int x, int y)
    {
        var lbl = new Label { Text = text, Location = new Point(x, y + 3), AutoSize = true };
        parent.Controls.Add(lbl);
        return lbl;
    }

    private void OnEndpointTypeChanged(object? sender, EventArgs e)
    {
        if (_endpointStationPc.Checked)
        {
            _stationTargetBox.Text = "127.0.0.1";
            _stationTargetBox.Enabled = false;
        }
        else
        {
            _stationTargetBox.Enabled = true;
            if (_stationTargetBox.Text == "127.0.0.1")
                _stationTargetBox.Text = "";
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Step 4: Extras (ancillary services)
    // ──────────────────────────────────────────────────────────────────────────────

    private void BuildStep4_Extras()
    {
        _stepTitle.Text = "Additional Services (optional)";
        _stepDescription.Text = "Select any additional services you want to forward. " +
            "These are independent of the radio and can be added to any profile.";

        _extrasList = new CheckedListBox
        {
            Location = new Point(0, 5),
            Size = new Size(575, 290),
            Font = new Font("Segoe UI", 9.5f),
            CheckOnClick = true
        };

        var services = CatalogLoader.GetServiceEntries(_catalog);
        foreach (var svc in services)
        {
            bool wasChecked = _selectedExtras.Contains(svc);
            _extrasList.Items.Add(svc, wasChecked);
        }

        _stepPanel.Controls.Add(_extrasList);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Step 5: Review & Apply
    // ──────────────────────────────────────────────────────────────────────────────

    private void BuildStep5_Review()
    {
        _stepTitle.Text = "Review & Apply";
        _stepDescription.Text = "Review the rules that will be created. Click Apply to add them to the Port Forwards grid.\r\n" +
            "These are default ports. If a device uses different ports, or you are adding a second device of the " +
            "same type, you can change the Client Port / Station Port in the Ham Router grid after the wizard finishes.";

        // Profile name
        var nameLabel = new Label
        {
            Text = "Profile name:",
            Location = new Point(0, 0),
            AutoSize = true
        };
        _stepPanel.Controls.Add(nameLabel);

        _profileNameBox = new TextBox
        {
            Location = new Point(100, 0),
            Size = new Size(300, 24),
            Text = string.IsNullOrEmpty(_profileName)
                ? $"{_selectedEntry?.Vendor} {_selectedEntry?.DisplayName}"
                : _profileName
        };
        _stepPanel.Controls.Add(_profileNameBox);

        // Enable immediately checkbox
        _enableImmediately = new CheckBox
        {
            Text = "Enable these rules immediately",
            Location = new Point(0, 30),
            AutoSize = true,
            Checked = _enableRules
        };
        _stepPanel.Controls.Add(_enableImmediately);

        // Build preview
        var preview = BuildPreviewRules();

        // Review grid
        _reviewGrid = new DataGridView
        {
            Location = new Point(0, 60),
            Size = new Size(575, 150),
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BorderStyle = BorderStyle.FixedSingle
        };
        _reviewGrid.Columns.Add("Name", "Name");
        _reviewGrid.Columns.Add("Dir", "Dir");
        _reviewGrid.Columns.Add("Proto", "Proto");
        _reviewGrid.Columns.Add("Client", "Client Port");
        _reviewGrid.Columns.Add("Target", "Station Target");
        _reviewGrid.Columns.Add("Station", "Station Port");

        foreach (var rule in preview)
        {
            string arrow = rule.Direction == "StationToClient" ? "\u2190" : "\u2192";
            _reviewGrid.Rows.Add(rule.Name, arrow, rule.Protocol, rule.ClientPort,
                $"{rule.StationTarget}", rule.StationPort);
        }
        _stepPanel.Controls.Add(_reviewGrid);

        // Conflict check
        var conflicts = ConflictDetector.Detect(preview, _existingRules, trialBind: true);
        if (conflicts.Count > 0)
        {
            string conflictText = string.Join("\r\n",
                conflicts.Select(c => $"[{c.Severity}] {c.Message}"));

            var conflictsBox = new TextBox
            {
                Text = conflictText,
                ForeColor = ConflictDetector.HasErrors(conflicts) ? Color.Red : Color.DarkOrange,
                BackColor = _stepPanel.BackColor,
                Font = new Font("Segoe UI", 8.5f),
                Location = new Point(0, 218),
                Size = new Size(575, 80),
                Multiline = true,
                WordWrap = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                ScrollBars = ScrollBars.Vertical,
                TabStop = false
            };
            _stepPanel.Controls.Add(conflictsBox);

            if (ConflictDetector.HasErrors(conflicts))
            {
                _nextButton.Enabled = false;
            }
        }
    }

    private List<ProfileForwardRule> BuildPreviewRules()
    {
        if (_selectedEntry is null) return new();

        if (_selectedEntry.IsGenericSerial)
        {
            // Serial bridge: single TCP forward rule using the configured TCP port
            int tcpPort = int.TryParse(_tcpPortBox?.Text, out int p) ? p : 4000;
            string deviceName = _deviceNameBox?.Text?.Trim() ?? "CAT Bridge";
            return new List<ProfileForwardRule>
            {
                new()
                {
                    Name = $"Serial-{deviceName}",
                    Protocol = "TCP",
                    Enabled = _enableRules,
                    BindAddress = "127.0.0.1",
                    ClientPort = tcpPort,
                    StationTarget = "127.0.0.1",
                    StationPort = tcpPort,
                    PortIdentity = "floating",
                    Role = "cat",
                    Direction = "ClientToStation",
                    Notes = $"Serial bridge for {deviceName}"
                }
            };
        }

        var profile = ProfileManager.BuildProfile(
            _selectedEntry,
            _profileName,
            _stationTarget,
            _enableRules,
            _selectedExtras.Count > 0 ? _selectedExtras : null);

        return profile.Forwards;
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Apply
    // ──────────────────────────────────────────────────────────────────────────────

    private void ApplyWizard()
    {
        if (_selectedEntry is null) return;

        _profileName = _profileNameBox.Text.Trim();
        _enableRules = _enableImmediately.Checked;

        WizardProfile profile;

        if (_selectedEntry.IsGenericSerial)
        {
            // Build serial bridge profile
            int tcpPort = int.TryParse(_tcpPortBox?.Text, out int p) ? p : 4000;
            int clientCom = int.TryParse(_clientComPortBox?.Text, out int c) ? c : 20;
            string stationCom = _stationComPortBox?.Text?.Trim() ?? "COM3";
            string deviceName = _deviceNameBox?.Text?.Trim() ?? "CAT Bridge";

            int baudRate = _baudCombo?.SelectedItem as int? ?? 9600;
            int dataBits = _dataBitsCombo?.SelectedItem as int? ?? 8;
            string parity = _parityCombo?.SelectedItem?.ToString() ?? "None";
            int stopBits = _stopBitsCombo?.SelectedItem as int? ?? 1;
            string dtr = _dtrCombo?.SelectedItem?.ToString() ?? "Off";
            string rts = _rtsCombo?.SelectedItem?.ToString() ?? "Off";

            profile = new WizardProfile
            {
                CreatedUtc = DateTime.UtcNow.ToString("o"),
                Profile = new ProfileInfo
                {
                    Name = _profileName,
                    CatalogId = "generic.serial-bridge",
                    Confidence = "verified"
                },
                SetupNotes = new SetupNotes
                {
                    Client = new List<string>
                    {
                        $"Install VSPE (or com0com + com2tcp) on this PC",
                        $"Load the generated client .vspe file, or create a TcpClient device:",
                        $"  Virtual COM port: COM{clientCom}",
                        $"  Target: 127.0.0.1:{tcpPort}",
                        $"Configure your logger to use COM{clientCom}"
                    },
                    Station = new List<string>
                    {
                        $"Install VSPE (or com2tcp) on the Station PC",
                        $"Load the generated station .vspe file, or create a TcpServer device:",
                        $"  Listen on: 0.0.0.0:{tcpPort}",
                        $"  Data source: {stationCom}",
                        $"  Baud: {baudRate}, {dataBits}{parity[0]}{stopBits}",
                        $"  DTR: {dtr}, RTS: {rts}"
                    },
                    Radio = new List<string>
                    {
                        $"Ensure radio CAT port matches: {baudRate} baud, {dataBits}{parity[0]}{stopBits}"
                    }
                },
                SerialBridge = new SerialBridgeInfo
                {
                    DeviceName = deviceName,
                    TcpPort = tcpPort,
                    ClientComPort = clientCom,
                    StationComPort = stationCom,
                    BaudRate = baudRate,
                    DataBits = dataBits,
                    Parity = parity,
                    StopBits = stopBits,
                    DtrControl = dtr,
                    RtsControl = rts,
                    PresetName = _selectedPreset?.Name ?? "Generic"
                }
            };

            profile.Forwards.Add(new ProfileForwardRule
            {
                Name = $"Serial-{deviceName}",
                Protocol = "TCP",
                Enabled = _enableRules,
                BindAddress = "127.0.0.1",
                ClientPort = tcpPort,
                StationTarget = "127.0.0.1",
                StationPort = tcpPort,
                PortIdentity = "floating",
                Role = "cat",
                Direction = "ClientToStation",
                Notes = $"Serial bridge: COM{clientCom} (client) <-> {stationCom} (station) via TCP {tcpPort}"
            });

            // Generate VSPE files
            try
            {
                var vspeConfig = new VspeGenerator.SerialBridgeConfig
                {
                    DeviceName = deviceName,
                    TcpPort = tcpPort,
                    ClientComPort = clientCom,
                    StationComPort = stationCom,
                    BaudRate = baudRate,
                    DataBits = dataBits,
                    Parity = parity,
                    StopBits = stopBits,
                    DtrControl = dtr,
                    RtsControl = rts,
                    PresetName = _selectedPreset?.Name ?? "Generic"
                };
                string baseName = ProfileManager.SanitizeFileName(_profileName);
                var files = VspeGenerator.WriteFiles(vspeConfig, baseName);
                profile.SetupNotes.VirtualSerial = new List<string>
                {
                    $"Client VSPE file: {files.ClientVspePath}",
                    $"Station VSPE file: {files.StationVspePath}",
                    $"com2tcp commands: {files.Com2TcpPath}"
                };
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Warning: Could not generate VSPE files: {ex.Message}",
                    "VSPE Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        else
        {
            // Standard radio/service profile
            profile = ProfileManager.BuildProfile(
                _selectedEntry,
                _profileName,
                _stationTarget,
                _enableRules,
                _selectedExtras.Count > 0 ? _selectedExtras : null);
        }

        // Run conflict detection.
        var conflicts = ConflictDetector.Detect(profile.Forwards, _existingRules, trialBind: false);
        if (ConflictDetector.HasErrors(conflicts))
        {
            MessageBox.Show(
                "Cannot apply: there are unresolved errors.\n\n" +
                string.Join("\n", conflicts.Where(c => c.Severity == ConflictSeverity.Error).Select(c => c.Message)),
                "Conflicts", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Save the profile.
        try
        {
            ProfileManager.SaveProfile(profile);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Warning: Could not save profile: {ex.Message}",
                "Save Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // Generate and open the readme.
        try
        {
            string readmePath = ReadmeGenerator.Generate(profile, conflicts);
            ReadmeGenerator.OpenInEditor(readmePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Warning: Could not generate setup guide: {ex.Message}",
                "Readme Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // Collect the rules for the caller.
        _appliedProfiles.Add(profile);
        GeneratedRules.AddRange(profile.Forwards);

        // Ask if they want to add another.
        var addAnother = MessageBox.Show(
            "Rules have been applied and the setup guide is open.\n\n" +
            "Do you want to add another radio or device?",
            "Add Another?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (addAnother == DialogResult.Yes)
        {
            // Reset for another pass.
            _selectedEntry = null;
            _stationTarget = "";
            _profileName = "";
            _enableRules = false;
            _selectedExtras.Clear();
            _selectedPreset = null;
            ShowStep(1);
        }
        else
        {
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
