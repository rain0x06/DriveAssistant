using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace FATXTools.Wpf;

public partial class ConsoleImageOpenWindow : Window
{
    private const string SwitchKeyHelp =
        "Switch key file example:\n" +
        "BIS KEY 0 (crypt): 00112233445566778899AABBCCDDEEFF\n" +
        "BIS KEY 0 (tweak): 00112233445566778899AABBCCDDEEFF\n" +
        "BIS KEY 1 (crypt): 00112233445566778899AABBCCDDEEFF\n" +
        "BIS KEY 1 (tweak): 00112233445566778899AABBCCDDEEFF\n" +
        "BIS KEY 2 (crypt): 00112233445566778899AABBCCDDEEFF\n" +
        "BIS KEY 2 (tweak): 00112233445566778899AABBCCDDEEFF\n" +
        "BIS KEY 3 (crypt): 00112233445566778899AABBCCDDEEFF\n" +
        "BIS KEY 3 (tweak): 00112233445566778899AABBCCDDEEFF\n\n" +
        "prod.keys names are also accepted, for example:\n" +
        "bis_key_03_crypt = 00112233445566778899AABBCCDDEEFF\n" +
        "bis_key_03_tweak = 00112233445566778899AABBCCDDEEFF\n\n" +
        "BIS 0 = PRODINFO/PRODINFOF, BIS 1 = SAFE, BIS 2 = SYSTEM, BIS 3 = USER.";

    private readonly IReadOnlyList<ConsoleImageKindOption> _options =
    [
        new("Auto detect", ConsoleDriveImageKind.Auto, false, string.Empty),
        new("Original Xbox FATX", ConsoleDriveImageKind.XboxOriginalFatx, false, string.Empty),
        new("Xbox 360 FATX", ConsoleDriveImageKind.Xbox360Fatx, false, string.Empty),
        new("Xbox One / Xbox Series GPT + NTFS", ConsoleDriveImageKind.XboxGptNtfs, false, string.Empty),
        new("Nintendo Switch NAND / eMMC", ConsoleDriveImageKind.NintendoSwitchNand, true, string.Empty),
        new("Nintendo Wii / GameCube", ConsoleDriveImageKind.NintendoWiiGameCube, false, string.Empty),
        new("Nintendo Wii U storage", ConsoleDriveImageKind.NintendoWiiUStorage, true, string.Empty),
        new("Nintendo DS / DSi / 3DS", ConsoleDriveImageKind.NintendoDs3ds, false, string.Empty),
        new("Generic NTFS / FAT32 / exFAT", ConsoleDriveImageKind.GenericFileSystem, false, string.Empty),
        new("Classic console + devkit media", ConsoleDriveImageKind.LegacyDevkitMedia, false, "PS1 save blocks and CD data tracks, PS2 memory-card images, PSP UMD ISO/CSO/PBP, PS Vita VPK, Dreamcast GDI/CIM/flash/VMU, Nintendo 64 ROM/save media, Game Boy-family ROM/save media, and Saturn system areas."),
        new("PlayStation 1 media / memory card", ConsoleDriveImageKind.PlayStation1Media, false, string.Empty),
        new("PlayStation 2 HDD", ConsoleDriveImageKind.PlayStation2Hdd, false, string.Empty),
        new("PlayStation 3 HDD", ConsoleDriveImageKind.PlayStation3Hdd, true, string.Empty),
        new("PlayStation 4 / PlayStation 4 Pro HDD", ConsoleDriveImageKind.PlayStation4Hdd, true, string.Empty)
    ];
    private TextBox? _filesystemTypeTextBox;
    private bool _suppressFilesystemFilterRefresh;

    public ConsoleImageOpenWindow(string? imagePath = null, ConsoleDriveImageKind initialKind = ConsoleDriveImageKind.Auto)
    {
        InitializeComponent();
        RefreshImageKindOptions(initialKind);
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            ImagePathTextBox.Text = imagePath;
        }

        UpdateKeyVisibility();
        UpdateStatus();
    }

    public string ImagePath => ImagePathTextBox.Text.Trim();

    public string KeyPath => KeyPathTextBox.Text.Trim();

    public ConsoleDriveImageKind ImageKind => (ImageKindComboBox.SelectedItem as ConsoleImageKindOption)?.Kind
        ?? ConsoleDriveImageKind.Auto;

    private void BrowseImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Console Images (*.img;*.bin;*.cue;*.raw;*.imgc;*.iso;*.cso;*.pbp;*.vpk;*.rvz;*.wbfs;*.zip;*.wud;*.wux;*.gcm;*.gdi;*.cdi;*.cim;*.vmu;*.vms;*.dci;*.hex;*.mcr;*.mcd;*.psx;*.ps2;*.z64;*.n64;*.v64;*.rom;*.sra;*.eep;*.fla;*.mpk;*.gb;*.gbc;*.gba;*.sav;*.nds;*.dsi;*.3ds;*.cci;*.cxi;*.cfa;*.csu;*.app)|*.img;*.bin;*.cue;*.raw;*.imgc;*.iso;*.cso;*.pbp;*.vpk;*.rvz;*.wbfs;*.zip;*.wud;*.wux;*.gcm;*.gdi;*.cdi;*.cim;*.vmu;*.vms;*.dci;*.hex;*.mcr;*.mcd;*.psx;*.ps2;*.z64;*.n64;*.v64;*.rom;*.sra;*.eep;*.fla;*.mpk;*.gb;*.gbc;*.gba;*.sav;*.nds;*.dsi;*.3ds;*.cci;*.cxi;*.cfa;*.csu;*.app|Switch NAND (*.bin)|*.bin|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            ImagePathTextBox.Text = dialog.FileName;
        }
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "All files (*.*)|*.*|Key files (*.bin;*.key;*.dat;*.txt;*.gz;*.tgz;prod.keys;keys.txt;eid_root_key;otp.bin;seeprom.bin;common-key;sd-key;sd-iv)|*.bin;*.key;*.dat;*.txt;*.gz;*.tgz;prod.keys;keys.txt;eid_root_key;otp.bin;seeprom.bin;common-key;sd-key;sd-iv",
            FilterIndex = 1,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            KeyPathTextBox.Text = dialog.FileName;
        }
    }

    private void ImageKindComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateKeyVisibility();
        UpdateFilesystemTypeHint();
        UpdateStatus();
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateStatus();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(ImagePath))
        {
            SetStatus("Select an existing disk image.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(KeyPath) && !File.Exists(KeyPath) && !Directory.Exists(KeyPath))
        {
            SetStatus("Selected key file does not exist.");
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowMaximized();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // DragMove can throw if mouse capture changes during the drag.
            }
        }
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreWindow_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowMaximized();
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ToggleWindowMaximized()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void UpdateKeyVisibility()
    {
        var visibility = RequiresKey(ImageKind) ? Visibility.Visible : Visibility.Collapsed;
        KeyPathTextBox.Visibility = visibility;
        BrowseKeyButton.Visibility = visibility;
        KeyPathTextBox.ToolTip = ImageKind == ConsoleDriveImageKind.NintendoSwitchNand
            ? CreateWrappedToolTip(SwitchKeyHelp)
            : "Optional for already-decrypted PlayStation images.";
    }

    private void UpdateStatus()
    {
        if (!string.IsNullOrWhiteSpace(ImagePath) && !File.Exists(ImagePath))
        {
            SetStatus("Image path does not exist.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(KeyPath) && !File.Exists(KeyPath) && !Directory.Exists(KeyPath))
        {
            SetStatus("Selected key file does not exist.");
            return;
        }

        SetStatus(string.Empty);
    }

    private static bool RequiresKey(ConsoleDriveImageKind kind)
    {
        return kind is ConsoleDriveImageKind.PlayStation3Hdd
            or ConsoleDriveImageKind.PlayStation4Hdd
            or ConsoleDriveImageKind.NintendoSwitchNand
            or ConsoleDriveImageKind.NintendoWiiUStorage;
    }

    private void RefreshImageKindOptions(ConsoleDriveImageKind? preferred = null)
    {
        var previousKind = preferred
            ?? (ImageKindComboBox.SelectedItem as ConsoleImageKindOption)?.Kind
            ?? ConsoleDriveImageKind.Auto;
        var filterText = _filesystemTypeTextBox?.Text?.Trim() ?? string.Empty;
        var rows = _options
            .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .Where(option => string.IsNullOrWhiteSpace(filterText)
                             || option.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (rows.Count == 0)
        {
            rows = _options.OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        _suppressFilesystemFilterRefresh = true;
        ImageKindComboBox.ItemsSource = rows;
        ImageKindComboBox.SelectedItem = rows.FirstOrDefault(option => option.Kind == previousKind) ?? rows[0];
        _suppressFilesystemFilterRefresh = false;
        UpdateFilesystemTypeHint();
    }

    private void UpdateFilesystemTypeHint()
    {
        const string typingHint = "Select a filesystem type from the list.";
        if (ImageKindComboBox.SelectedItem is ConsoleImageKindOption legacyOption &&
            legacyOption.Kind == ConsoleDriveImageKind.LegacyDevkitMedia &&
            !string.IsNullOrWhiteSpace(legacyOption.HelpText))
        {
            ImageKindComboBox.ToolTip = CreateWrappedToolTip($"{typingHint}\n\n{legacyOption.HelpText}");
            return;
        }

        ImageKindComboBox.ToolTip = CreateWrappedToolTip(typingHint);
    }

    private static ToolTip CreateWrappedToolTip(string text)
    {
        return new ToolTip
        {
            MaxWidth = 560,
            Content = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12
            }
        };
    }

    private void SetStatus(string text)
    {
        StatusTextBlock.Text = text;
        StatusTextBlock.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ImageKindComboBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (_filesystemTypeTextBox != null)
        {
            return;
        }

        _filesystemTypeTextBox = ImageKindComboBox.Template.FindName("PART_EditableTextBox", ImageKindComboBox) as TextBox;
        if (_filesystemTypeTextBox == null)
        {
            return;
        }

        _filesystemTypeTextBox.TextChanged += FilesystemTypeTextBox_TextChanged;
        _filesystemTypeTextBox.GotKeyboardFocus += (_, _) => ImageKindComboBox.IsDropDownOpen = true;
        _filesystemTypeTextBox.ToolTip = CreateWrappedToolTip("Select a filesystem type from the list.");
    }

    private void FilesystemTypeTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressFilesystemFilterRefresh)
        {
            return;
        }

        var currentKind = ImageKind;
        RefreshImageKindOptions(currentKind);
        ImageKindComboBox.IsDropDownOpen = true;
    }
}

public enum ConsoleDriveImageKind
{
    Auto,
    XboxOriginalFatx,
    Xbox360Fatx,
    XboxGptNtfs,
    NintendoSwitchNand,
    NintendoWiiGameCube,
    NintendoWiiUStorage,
    NintendoDs3ds,
    GenericFileSystem,
    PlayStation1Media,
    PlayStation2Hdd,
    PlayStation3Hdd,
    PlayStation4Hdd,
    LegacyDevkitMedia
}

public sealed record ConsoleImageKindOption(
    string Name,
    ConsoleDriveImageKind Kind,
    bool RequiresKey,
    string HelpText)
{
    public override string ToString()
    {
        return Name;
    }
}
