using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FamilyDnsLock.Core;

namespace FamilyDnsLock.App;

public partial class MainWindow : Window
{
    private readonly IpcClient _ipc = new();
    private readonly DispatcherTimer _refreshTimer = new();
    private StatusDto? _lastStatus;
    private Action<string>? _modalPinCallback;

    public MainWindow()
    {
        InitializeComponent();

        _refreshTimer.Interval = TimeSpan.FromSeconds(2);
        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _refreshTimer.Start();

        RbCustom.Checked += (_, _) => PanelCustomDuration.Visibility = Visibility.Visible;
        RbCustom.Unchecked += (_, _) => PanelCustomDuration.Visibility = Visibility.Collapsed;

        RbProviderCustom.Checked += (_, _) => PanelCustomProvider.Visibility = Visibility.Visible;
        RbProviderCustom.Unchecked += (_, _) => PanelCustomProvider.Visibility = Visibility.Collapsed;

        DpCustomDate.SelectedDate = DateTime.Today.AddDays(7);

        Loaded += async (_, _) =>
        {
            await RefreshStatusAsync();
            await RefreshDiagnosticsAsync();
        };
    }

    private async Task RefreshStatusAsync()
    {
        var response = await _ipc.SendAsync(new IpcRequest(IpcCommandType.GetStatus));
        if (!response.Success || response.Status is null)
        {
            AlertBar.Visibility = Visibility.Visible;
            AlertText.Text = $"Background Service Disconnected: {response.Message}. Is FamilyDnsLock service running?";
            StatusBadge.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38)); // Red
            StatusBadgeText.Text = "OFFLINE";
            TxtCurrentStatus.Text = "Service Offline";
            TxtLastUpdated.Text = $"Last attempt: {DateTime.Now:T} (Failed)";
            return;
        }

        AlertBar.Visibility = Visibility.Collapsed;
        _lastStatus = response.Status;
        TxtLastUpdated.Text = $"Connected • {DateTime.Now:T}";

        // Update Dashboard
        TxtCurrentProvider.Text = _lastStatus.ProviderName;
        TxtExpiryAction.Text = _lastStatus.ExpiryAction switch
        {
            ExpiryAction.KeepFilteredDns => "Keep Filtered DNS active",
            ExpiryAction.RestorePreviousDns => "Restore Previous DNS settings",
            _ => "Not active"
        };

        if (_lastStatus.IsActive)
        {
            StatusBadge.Background = new SolidColorBrush(Color.FromRgb(22, 163, 74)); // Green
            StatusBadgeText.Text = "PROTECTION ACTIVE";
            TxtCurrentStatus.Text = "Active (Enforced)";
            TxtCurrentStatus.Foreground = new SolidColorBrush(Color.FromRgb(22, 163, 74));
            BtnEarlyUnlock.Visibility = Visibility.Visible;
            BtnGoToActivate.IsEnabled = false;

            if (_lastStatus.ExpiresUtc.HasValue)
            {
                TxtExpiresTime.Text = $"{_lastStatus.ExpiresUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} (Local)";
            }

            if (_lastStatus.Remaining.HasValue && _lastStatus.Remaining.Value > TimeSpan.Zero)
            {
                var r = _lastStatus.Remaining.Value;
                TxtRemainingTime.Text = $"{r.Days}d {r.Hours}h {r.Minutes}m {r.Seconds}s";
            }
            else
            {
                TxtRemainingTime.Text = "Expiring shortly...";
            }
        }
        else
        {
            if (_lastStatus.Status == ProtectionStatus.ExpiredFilteredDnsRetained)
            {
                StatusBadge.Background = new SolidColorBrush(Color.FromRgb(59, 130, 246)); // Blue
                StatusBadgeText.Text = "EXPIRED (FILTER RETAINED)";
                TxtCurrentStatus.Text = "Expired (Family DNS still applied)";
                TxtCurrentStatus.Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246));
            }
            else if (_lastStatus.Status == ProtectionStatus.ExpiredDnsRestored)
            {
                StatusBadge.Background = new SolidColorBrush(Color.FromRgb(100, 116, 139)); // Slate
                StatusBadgeText.Text = "EXPIRED (DNS RESTORED)";
                TxtCurrentStatus.Text = "Expired (Previous DNS restored)";
                TxtCurrentStatus.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139));
            }
            else
            {
                StatusBadge.Background = new SolidColorBrush(Color.FromRgb(100, 116, 139));
                StatusBadgeText.Text = "INACTIVE";
                TxtCurrentStatus.Text = "Inactive (No active lock)";
                TxtCurrentStatus.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139));
            }

            TxtRemainingTime.Text = "No active lock";
            TxtExpiresTime.Text = "-";
            BtnEarlyUnlock.Visibility = Visibility.Collapsed;
            BtnGoToActivate.IsEnabled = true;
        }

        // Activate Lock PIN Hint
        TxtActivatePinHint.Text = _lastStatus.IsPinConfigured
            ? "(Parent PIN required to activate)"
            : "(No PIN set yet; you can set one in Settings)";
    }

    private void BtnGoToActivate_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 1;
    }

    private async void BtnRefreshStatus_Click(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync();
    }

    private async void BtnConfirmActivate_Click(object sender, RoutedEventArgs e)
    {
        int? days = null;
        DateTimeOffset? customExpiry = null;

        if (Rb1Day.IsChecked == true) days = 1;
        else if (Rb7Days.IsChecked == true) days = 7;
        else if (Rb15Days.IsChecked == true) days = 15;
        else if (Rb30Days.IsChecked == true) days = 30;
        else if (Rb60Days.IsChecked == true) days = 60;
        else if (Rb90Days.IsChecked == true) days = 90;
        else if (RbCustom.IsChecked == true)
        {
            if (!DpCustomDate.SelectedDate.HasValue)
            {
                MessageBox.Show("Please select a valid custom date.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtCustomHours.Text, out var h) || h < 0 || h > 23 ||
                !int.TryParse(TxtCustomMinutes.Text, out var m) || m < 0 || m > 59)
            {
                MessageBox.Show("Please enter valid custom hours (0-23) and minutes (0-59).", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var d = DpCustomDate.SelectedDate.Value;
            var dt = new DateTime(d.Year, d.Month, d.Day, h, m, 0, DateTimeKind.Local);
            if (dt <= DateTime.Now)
            {
                MessageBox.Show("Custom expiry must be in the future.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            customExpiry = new DateTimeOffset(dt).ToUniversalTime();
        }

        var expiryAction = RbRestorePrevious.IsChecked == true
            ? ExpiryAction.RestorePreviousDns
            : ExpiryAction.KeepFilteredDns;

        var pin = PbActivatePin.Password;

        var request = new IpcRequest(
            IpcCommandType.Activate,
            Pin: string.IsNullOrEmpty(pin) ? null : pin,
            DurationDays: days,
            CustomExpiresUtc: customExpiry,
            ExpiryAction: expiryAction);

        var response = await _ipc.SendAsync(request);
        if (response.Success)
        {
            MessageBox.Show("Protection lock activated successfully! DNS and browser security controls are now enforced.", "Protection Active", MessageBoxButton.OK, MessageBoxImage.Information);
            PbActivatePin.Clear();
            await RefreshStatusAsync();
            MainTabs.SelectedIndex = 0;
        }
        else
        {
            MessageBox.Show($"Failed to activate lock: {response.Message}", "Activation Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnEarlyUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (_lastStatus?.IsPinConfigured == true)
        {
            ShowPinModal("Early Unlock Authorization", "Enter the parent PIN to release this timed lock.", async (pin) =>
            {
                var response = await _ipc.SendAsync(new IpcRequest(IpcCommandType.EarlyUnlock, Pin: pin));
                if (response.Success)
                {
                    MessageBox.Show("Protection lock released early.", "Unlocked", MessageBoxButton.OK, MessageBoxImage.Information);
                    await RefreshStatusAsync();
                }
                else
                {
                    MessageBox.Show($"Early unlock failed: {response.Message}", "Authorization Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            });
        }
        else
        {
            var res = MessageBox.Show("No parent PIN is configured. Release protection now?", "Confirm Unlock", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                var response = await _ipc.SendAsync(new IpcRequest(IpcCommandType.EarlyUnlock));
                if (response.Success)
                {
                    MessageBox.Show("Protection lock released.", "Unlocked", MessageBoxButton.OK, MessageBoxImage.Information);
                    await RefreshStatusAsync();
                }
                else
                {
                    MessageBox.Show($"Early unlock failed: {response.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }

    private async void BtnSavePin_Click(object sender, RoutedEventArgs e)
    {
        var currentPin = PbCurrentPin.Password;
        var newPin = PbNewPin.Password;
        var confirmPin = PbConfirmNewPin.Password;

        if (string.IsNullOrWhiteSpace(newPin) || newPin.Length < 4 || newPin.Length > 16)
        {
            MessageBox.Show("New PIN must be between 4 and 16 digits.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (newPin != confirmPin)
        {
            MessageBox.Show("New PIN and Confirm PIN do not match.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var request = new IpcRequest(
            IpcCommandType.SetPin,
            Pin: string.IsNullOrEmpty(currentPin) ? null : currentPin,
            NewPin: newPin);

        var response = await _ipc.SendAsync(request);
        if (response.Success)
        {
            PbCurrentPin.Clear();
            PbNewPin.Clear();
            PbConfirmNewPin.Clear();

            if (!string.IsNullOrEmpty(response.GeneratedRecoveryKey))
            {
                BoxRecoveryKey.Visibility = Visibility.Visible;
                TxtRecoveryKeyDisplay.Text = response.GeneratedRecoveryKey;
                MessageBox.Show("Parent PIN saved successfully!\n\nPlease write down your Emergency Recovery Key displayed on screen.", "PIN Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Parent PIN updated successfully.", "PIN Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            await RefreshStatusAsync();
        }
        else
        {
            MessageBox.Show($"Failed to save PIN: {response.Message}", "PIN Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnRecoverPin_Click(object sender, RoutedEventArgs e)
    {
        var recoveryKey = TxtInputRecoveryKey.Text?.Trim();
        var newPin = PbRecoverNewPin.Password;

        if (string.IsNullOrWhiteSpace(recoveryKey))
        {
            MessageBox.Show("Please enter your emergency recovery key.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(newPin) || newPin.Length < 4)
        {
            MessageBox.Show("New PIN must be at least 4 digits.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var request = new IpcRequest(
            IpcCommandType.RecoverPin,
            RecoveryKey: recoveryKey,
            NewPin: newPin);

        var response = await _ipc.SendAsync(request);
        if (response.Success)
        {
            TxtInputRecoveryKey.Clear();
            PbRecoverNewPin.Clear();

            if (!string.IsNullOrEmpty(response.GeneratedRecoveryKey))
            {
                BoxRecoveryKey.Visibility = Visibility.Visible;
                TxtRecoveryKeyDisplay.Text = response.GeneratedRecoveryKey;
            }

            MessageBox.Show("PIN reset successfully! Your new recovery key is displayed on the settings page.", "Recovery Success", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshStatusAsync();
        }
        else
        {
            MessageBox.Show($"Recovery failed: {response.Message}", "Recovery Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnSaveProvider_Click(object sender, RoutedEventArgs e)
    {
        if (RbProviderCleanBrowsing.IsChecked == true)
        {
            var defaultProvider = DnsProvider.CleanBrowsingFamily;
            var req = new IpcRequest(
                IpcCommandType.UpdateProvider,
                ProviderName: defaultProvider.Name,
                ProviderServers: defaultProvider.Servers.Select(s => s.ToString()).ToArray());

            var res = await _ipc.SendAsync(req);
            if (res.Success)
            {
                MessageBox.Show("Provider reset to CleanBrowsing Family Filter.", "Provider Updated", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshStatusAsync();
            }
            else
            {
                MessageBox.Show($"Failed: {res.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            var name = TxtCustomProviderName.Text?.Trim();
            var serversStr = TxtCustomProviderServers.Text;
            var servers = serversStr?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (string.IsNullOrWhiteSpace(name) || servers is null || servers.Length == 0)
            {
                MessageBox.Show("Please provide a name and at least one IPv4 and one IPv6 address.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var req = new IpcRequest(
                IpcCommandType.UpdateProvider,
                ProviderName: name,
                ProviderServers: servers);

            var res = await _ipc.SendAsync(req);
            if (res.Success)
            {
                MessageBox.Show($"Provider updated to '{name}'.", "Provider Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshStatusAsync();
            }
            else
            {
                MessageBox.Show($"Failed: {res.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void BtnRefreshDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDiagnosticsAsync();
    }

    private async Task RefreshDiagnosticsAsync()
    {
        var response = await _ipc.SendAsync(new IpcRequest(IpcCommandType.GetDiagnostics));
        if (response.Success && response.Diagnostics is not null)
        {
            var d = response.Diagnostics;

            var tableRows = d.Adapters.Select(a => new
            {
                a.InterfaceIndex,
                a.Name,
                a.Status,
                CurrentDnsSummary = a.CurrentDns.Count > 0 ? string.Join(", ", a.CurrentDns) : "None (DHCP/Default)",
                MatchSummary = a.DnsMatchesConfigured ? "✔ Yes" : "✘ No"
            }).ToList();

            DgAdapters.ItemsSource = tableRows;

            TxtDiagFirewall.Text = d.FirewallActive
                ? $"Enforced ({d.ActiveFirewallRules.Count} rules active: Port 853 DoT Blocked, Unapproved Port 53 Blocked)"
                : "Not enforced (Inactive)";

            TxtDiagBrowser.Text = d.BrowserPoliciesActive
                ? "Enforced (Edge, Chrome, Brave, Firefox DoH disabled via Group Policy)"
                : "Not active";

            TxtServiceLogTail.Text = d.ServiceLogTail;
        }
    }

    private void ShowPinModal(string title, string subtitle, Action<string> onConfirmed)
    {
        ModalTitle.Text = title;
        ModalSubtitle.Text = subtitle;
        PbModalPin.Clear();
        ModalErrorText.Visibility = Visibility.Collapsed;
        _modalPinCallback = onConfirmed;
        ModalPinOverlay.Visibility = Visibility.Visible;
        PbModalPin.Focus();
    }

    private void BtnModalCancel_Click(object sender, RoutedEventArgs e)
    {
        ModalPinOverlay.Visibility = Visibility.Collapsed;
        _modalPinCallback = null;
    }

    private void BtnModalConfirm_Click(object sender, RoutedEventArgs e)
    {
        var pin = PbModalPin.Password;
        if (string.IsNullOrWhiteSpace(pin))
        {
            ModalErrorText.Text = "PIN is required.";
            ModalErrorText.Visibility = Visibility.Visible;
            return;
        }

        ModalPinOverlay.Visibility = Visibility.Collapsed;
        _modalPinCallback?.Invoke(pin);
        _modalPinCallback = null;
    }
}
