using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PegLine.Core;
using PegLine.Hosting;
using PegLine.Platform;
using PegLine.ViewModels;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace PegLine.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly AppHost _host;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private bool _loading = true;
    private bool _placed;

    public SettingsWindow(AppHost host)
    {
        InitializeComponent();
        _host = host;
        (_hwnd, _appWindow) = WindowChrome.Attach(this);
        Title = "挂绳设置";
        Activated += OnActivated;
        Reload();
    }

    public void Reload()
    {
        _loading = true;
        var settings = _host.Settings;
        DefaultFolderSwitch.IsOn = settings.IncludeDefaultScreenshotsFolder;
        DefaultFolderPath.Text = AppSettings.DefaultScreenshotsFolder();
        DefaultFolderState.Text = Directory.Exists(AppSettings.DefaultScreenshotsFolder())
            ? "文件夹在。"
            : "还没有这个文件夹。打开开关后会创建它。";
        FolderList.ItemsSource = _host.FolderRows();
        ClipboardSwitch.IsOn = settings.WatchClipboard;
        DeleteSwitch.IsOn = settings.DeleteFileOnRemove;
        var slider = Math.Clamp(settings.MaxPegs, (int)MaxPegsSlider.Minimum, (int)MaxPegsSlider.Maximum);
        MaxPegsSlider.Value = slider;
        MaxPegsValue.Text = settings.MaxPegs.ToString();
        HotkeyBox.Text = _host.HotkeyLabel;
        HotkeyStatus.Text = _host.HotkeyStatusText;
        ScreenshotHotkeyBox.Text = _host.ScreenshotHotkeyLabel;
        ScreenshotHotkeyStatus.Text = _host.ScreenshotHotkeyStatusText;
        SettingsPathText.Text = new SettingsStore().FilePath;
        _loading = false;
    }

    private void OnActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs e)
    {
        if (_placed || e.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        _placed = true;
        WindowChrome.PlaceSettings(_hwnd, _appWindow);
    }

    private void DefaultFolderSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _host.SetIncludeDefaultFolder(DefaultFolderSwitch.IsOn);
        DefaultFolderState.Text = Directory.Exists(AppSettings.DefaultScreenshotsFolder())
            ? "文件夹在。"
            : "还没有这个文件夹。";
    }

    private void ClipboardSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _host.SetWatchClipboard(ClipboardSwitch.IsOn);
        }
    }

    private void DeleteSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _host.SetDeleteFileOnRemove(DeleteSwitch.IsOn);
        }
    }

    private void MaxPegsSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var value = (int)Math.Round(e.NewValue);
        MaxPegsValue.Text = value.ToString();
        _host.SetMaxPegs(value);
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        var path = folder.Path;
        DispatcherQueue.TryEnqueue(() =>
        {
            _host.AddExtraFolder(path);
            FolderList.ItemsSource = _host.FolderRows();
        });
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FolderRow row })
        {
            _host.RemoveExtraFolder(row.FullPath);
            FolderList.ItemsSource = _host.FolderRows();
        }
    }

    private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _host.PauseHotkey();
        HotkeyStatus.Text = "按下新的组合键。至少要有 Ctrl、Alt、Shift 或 Win。";
    }

    private void HotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _host.ResumeHotkey();
        HotkeyStatus.Text = _host.HotkeyStatusText;
        ScreenshotHotkeyStatus.Text = _host.ScreenshotHotkeyStatusText;
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;
        if (e.Key is VirtualKey.Escape or VirtualKey.Tab)
        {
            HotkeyBox.Text = _host.HotkeyLabel;
            return;
        }

        if (IsModifier(e.Key))
        {
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (IsDown(VirtualKey.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (IsDown(VirtualKey.Menu))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (IsDown(VirtualKey.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        if (!HotkeyGesture.TryFromVirtualKey(modifiers, (int)e.Key, out var gesture))
        {
            HotkeyStatus.Text = "这个键不能当热键。试 Ctrl + Alt + 字母。";
            return;
        }

        HotkeyBox.Text = gesture.ToDisplayString();
        if (_host.TrySetHotkey(gesture, out var error))
        {
            HotkeyStatus.Text = "已登记 " + gesture.ToDisplayString() + "。";
        }
        else
        {
            HotkeyStatus.Text = error ?? "这个热键被其他程序占用了。";
        }
    }




    private void Done_Click(object sender, RoutedEventArgs e) => Close();


    private void ScreenshotHotkeyBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;
        if (e.Key is VirtualKey.Escape or VirtualKey.Tab)
        {
            ScreenshotHotkeyBox.Text = _host.ScreenshotHotkeyLabel;
            return;
        }

        if (IsModifier(e.Key))
        {
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (IsDown(VirtualKey.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (IsDown(VirtualKey.Menu))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (IsDown(VirtualKey.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        if (!HotkeyGesture.TryFromVirtualKey(modifiers, (int)e.Key, out var gesture))
        {
            ScreenshotHotkeyStatus.Text = "这个键不能当热键。试 Ctrl + Shift + 字母。";
            return;
        }

        ScreenshotHotkeyBox.Text = gesture.ToDisplayString();
        if (_host.TrySetScreenshotHotkey(gesture, out var error))
        {
            ScreenshotHotkeyStatus.Text = "已登记 " + gesture.ToDisplayString() + "。截完会自动挂上绳。";
        }
        else
        {
            ScreenshotHotkeyStatus.Text = error ?? "这个热键被其他程序占用了。";
        }
    }

    private static bool IsModifier(VirtualKey key) =>
        key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
            or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static bool IsDown(VirtualKey key)
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }
}
