using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ServiceProcess;
using System.Windows;
using Microsoft.Win32;

namespace Systema.Views;

public partial class ServicePickerDialog : Window
{
    public IReadOnlyList<string> SelectedServices { get; private set; } = Array.Empty<string>();

    /// <summary>Services already in the kill list — these are hidden from the picker so users
    /// don't see duplicates or get confused about what's already added.</summary>
    public HashSet<string> ExistingServices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly ObservableCollection<ServicePickerItem> _allItems = new();
    private string _filter = string.Empty;

    public ServicePickerDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Load services on background thread, then populate UI
        Task.Run(LoadServices).ContinueWith(t =>
        {
            if (t.Exception != null) return;
            Dispatcher.Invoke(ApplyFilter);
        });
    }

    /// <summary>Reads the service description string from the registry (fast, no WMI needed).</summary>
    private static string GetServiceDescription(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            return key?.GetValue("Description") as string ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    private void LoadServices()
    {
        try
        {
            var services = ServiceController.GetServices()
                .Select(s =>
                {
                    try
                    {
                        return new ServicePickerItem
                        {
                            ServiceName = s.ServiceName,
                            DisplayName = s.DisplayName,
                            Description = GetServiceDescription(s.ServiceName),
                            Status      = s.Status.ToString()
                        };
                    }
                    catch { return null; }
                    finally { s.Dispose(); }
                })
                .Where(x => x != null && !ExistingServices.Contains(x!.ServiceName))
                .OrderBy(x => x!.ServiceName)
                .ToList();

            Dispatcher.Invoke(() =>
            {
                _allItems.Clear();
                foreach (var item in services)
                {
                    item!.PropertyChanged += OnItemSelectionChanged;
                    _allItems.Add(item);
                }
                ApplyFilter();
            });
        }
        catch { /* silently ignore if service enumeration fails */ }
    }

    private void OnItemSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServicePickerItem.IsSelected))
            UpdateSelectionCount();
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _filter = SearchBox.Text.Trim();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filtered = string.IsNullOrEmpty(_filter)
            ? _allItems.ToList()
            : _allItems.Where(i =>
                i.ServiceName.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
                i.DisplayName.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
                i.Description.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();

        ServiceList.ItemsSource = filtered;
        UpdateSelectionCount();
    }

    private void UpdateSelectionCount()
    {
        int count = _allItems.Count(i => i.IsSelected);
        SelectionCount.Text = count > 0 ? $"{count} selected" : string.Empty;
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedServices = _allItems.Where(i => i.IsSelected).Select(i => i.ServiceName).ToList();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

public class ServicePickerItem : INotifyPropertyChanged
{
    public string ServiceName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Status      { get; init; } = string.Empty;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
