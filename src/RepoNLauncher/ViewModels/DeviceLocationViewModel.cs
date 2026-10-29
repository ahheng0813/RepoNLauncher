using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoNLauncher.Models;
using RepoNLauncher.Services;

namespace RepoNLauncher.ViewModels;

/// <summary>
/// The Phone screen's Location tools: gives the selected device a virtual location, pinned on the map, found by name, typed
/// or this PC's own, and gives a phone its real one back. One phone at a time keeps a virtual location; the dashboard gives
/// it back on closing.
/// </summary>
public partial class DeviceLocationViewModel : ObservableObject
{
    // Often enough for an app that waits for a fresh position (as maps do) to get one; a real GPS reports every second.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    // Coordinates being typed go on the map once typing stops for this long, so the map doesn't jump about on the way.
    private static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(450);

    // How close in a place typed, found or this PC's is shown, unless the map is closer in already.
    private const double CloseUpZoom = 15;

    private readonly IDeviceToolsService _tools;
    private readonly Func<AndroidDevice?> _selectedDevice;

    // The phone whose location is virtual now, and where it is. Emulators aren't tracked: moving their GPS lasts by itself.
    private AndroidDevice? _phone;
    private (double Latitude, double Longitude) _phoneLocation;
    private CancellationTokenSource? _refresh;

    private CancellationTokenSource? _typing;
    private bool _matchingPin; // Coordinates and Pin being set to the same place, so neither follows the other back

    public DeviceLocationViewModel(IDeviceToolsService tools, Func<AndroidDevice?> selectedDevice)
    {
        _tools = tools;
        _selectedDevice = selectedDevice;
    }

    /// <summary>"Latitude, longitude", as Google Maps copies them: the map's pin, or typed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetCommand))]
    private string _coordinates = string.Empty;

    /// <summary>The map's pin: put there on the map, or the coordinates typed, the place found or this PC's.</summary>
    [ObservableProperty] private GeoPoint? _pin;

    [ObservableProperty] private double _mapZoom = 2;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private bool _isSearching;

    /// <summary>How the last search went, when it found nothing or couldn't be made.</summary>
    [ObservableProperty] private string _searchNote = string.Empty;

    /// <summary>The last search's matches; the one on the map is chosen.</summary>
    public ObservableCollection<PlaceChoice> Places { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetCommand), nameof(UsePcLocationCommand), nameof(UseRealLocationCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _status = string.Empty;

    /// <summary>A phone has a virtual location that it can be given back its real one from.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseRealLocationCommand))]
    private bool _hasVirtualPhoneLocation;

    private bool CanSet() => !IsBusy && TryParse(Coordinates, out _, out _);

    [RelayCommand(CanExecute = nameof(CanSet))]
    private async Task SetAsync()
    {
        if (!TryParse(Coordinates, out var latitude, out var longitude)) return;
        ShowTypedOnMap();
        await MoveToAsync(latitude, longitude, Format(latitude, longitude));
    }

    private bool CanSearch() => !IsSearching && !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>Finds the place named in the search box and puts the best match on the map; coordinates go straight there.</summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        var text = SearchText.Trim();
        if (TryParse(text, out var latitude, out var longitude))
        {
            Places.Clear();
            SearchNote = string.Empty;
            ShowOnMap(latitude, longitude, Math.Max(MapZoom, CloseUpZoom));
            return;
        }

        IsSearching = true;
        SearchNote = $"Looking for {text}…";
        try
        {
            var places = await PlaceSearch.SearchAsync(text);
            Places.Clear();
            foreach (var place in places) Places.Add(new PlaceChoice(place));
            SearchNote = places.Count == 0 ? $"Nothing called {text} found. Try adding the town or country." : string.Empty;
            if (Places.Count > 0) ChoosePlace(Places[0]);
        }
        catch (InvalidOperationException e)
        {
            SearchNote = e.Message;
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>Puts one of the search's matches on the map.</summary>
    [RelayCommand]
    private void ChoosePlace(PlaceChoice choice)
    {
        foreach (var place in Places) place.IsChosen = place == choice;
        ShowOnMap(choice.Place.Latitude, choice.Place.Longitude, choice.Place.Zoom);
    }

    partial void OnSearchTextChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) return;
        Places.Clear();
        SearchNote = string.Empty;
    }

    partial void OnCoordinatesChanged(string value)
    {
        if (_matchingPin) return;
        _typing?.Cancel();
        var typing = _typing = new CancellationTokenSource();
        _ = ShowAfterTypingAsync(typing.Token);
    }

    private async Task ShowAfterTypingAsync(CancellationToken typing)
    {
        try
        {
            await Task.Delay(TypingPause, typing);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        ShowTypedOnMap();
    }

    // The pin was put somewhere on the map: the coordinates follow it.
    partial void OnPinChanged(GeoPoint? value)
    {
        if (_matchingPin || value is not { } pin) return;
        _typing?.Cancel();
        foreach (var place in Places) place.IsChosen = false;
        _matchingPin = true;
        try
        {
            Coordinates = Format(pin.Latitude, pin.Longitude);
        }
        finally
        {
            _matchingPin = false;
        }
    }

    // The coordinates typed, if they're a place and not where the pin is already.
    private void ShowTypedOnMap()
    {
        _typing?.Cancel();
        if (!TryParse(Coordinates, out var latitude, out var longitude)) return;
        if (Pin is { } pin && Format(pin.Latitude, pin.Longitude) == Format(latitude, longitude)) return;
        foreach (var place in Places) place.IsChosen = false;
        ShowOnMap(latitude, longitude, Math.Max(MapZoom, CloseUpZoom));
    }

    private void ShowOnMap(double latitude, double longitude, double zoom)
    {
        _typing?.Cancel();
        _matchingPin = true;
        try
        {
            MapZoom = zoom;
            Coordinates = Format(latitude, longitude);
            // Afresh, so the map moves to the pin even when it's there already but out of view.
            Pin = null;
            Pin = new GeoPoint(latitude, longitude);
        }
        finally
        {
            _matchingPin = false;
        }
    }

    private bool CanUsePcLocation() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUsePcLocation))]
    private async Task UsePcLocationAsync()
    {
        IsBusy = true;
        Status = "Asking Windows where this PC is…";
        (double Latitude, double Longitude) here;
        try
        {
            here = await PcLocation.GetAsync();
        }
        catch (InvalidOperationException e)
        {
            Status = e.Message;
            IsBusy = false;
            return;
        }
        IsBusy = false;

        foreach (var place in Places) place.IsChosen = false;
        ShowOnMap(here.Latitude, here.Longitude, Math.Max(MapZoom, CloseUpZoom));
        await MoveToAsync(here.Latitude, here.Longitude, "this PC's location");
    }

    private bool CanUseRealLocation() => !IsBusy && HasVirtualPhoneLocation;

    [RelayCommand(CanExecute = nameof(CanUseRealLocation))]
    private async Task UseRealLocationAsync()
    {
        if (_phone is not { } phone) return;
        IsBusy = true;
        Status = $"Giving {phone.Model} its real location back…";
        try
        {
            await ClearPhoneAsync();
            Status = $"{phone.Model} reports where it really is again.";
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException)
        {
            Status = $"{phone.Model} still has the virtual location: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Opens Google Maps at the coordinates typed, to look around or pick another place.</summary>
    [RelayCommand]
    private void OpenMap()
    {
        var url = TryParse(Coordinates, out var latitude, out var longitude)
            ? $"https://www.google.com/maps/search/?api=1&query={Format(latitude, longitude).Replace(" ", "")}"
            : "https://www.google.com/maps";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>Gives the phone with a virtual location its real one back, so it doesn't stay somewhere else after the dashboard closes.</summary>
    public void Shutdown()
    {
        StopRefreshing();
        if (_phone is not { } phone) return;
        try
        {
            // The service never needs this (UI) thread to finish, so waiting on it here can't hang.
            _tools.ClearPhoneLocationAsync(phone.Serial).Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // unplugged, most likely; its test providers go when it restarts
        }
    }

    private async Task MoveToAsync(double latitude, double longitude, string what)
    {
        if (_selectedDevice() is not { } device)
        {
            Status = "Pick a device first.";
            return;
        }

        IsBusy = true;
        Status = $"Moving {device.Model} to {what}…";
        try
        {
            // One phone keeps a virtual location at a time; the one before gets its real location back.
            if (_phone != null && _phone.Serial != device.Serial)
            {
                try
                {
                    await ClearPhoneAsync();
                }
                catch (Exception e) when (e is InvalidOperationException or TimeoutException)
                {
                    // Unplugged, most likely; its test providers go when it restarts.
                    _phone = null;
                    HasVirtualPhoneLocation = false;
                }
            }

            var result = await _tools.SetLocationAsync(device.Serial, latitude, longitude);
            var where = Format(latitude, longitude);
            if (result.Kind == VirtualLocationKind.EmulatorGps)
            {
                Status = $"{device.Model}'s GPS is at {where}.";
                return;
            }

            _phone = device;
            _phoneLocation = (latitude, longitude);
            HasVirtualPhoneLocation = true;
            StartRefreshing();
            Status = result.IsLocationOff
                ? $"{device.Model} has a virtual location of {where}, but its location is off: turn it on in the phone's quick settings for apps to see it."
                : $"{device.Model} reports {where} to its apps until you give it its real location back, or close the dashboard.";
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException)
        {
            Status = $"Couldn't move {device.Model}: {e.Message.TrimEnd('.')}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void StartRefreshing()
    {
        StopRefreshing();
        var refresh = _refresh = new CancellationTokenSource();
        var serial = _phone!.Serial;
        var (latitude, longitude) = _phoneLocation;
        _ = Task.Run(async () =>
        {
            while (!refresh.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(RefreshInterval, refresh.Token);
                    await _tools.RefreshPhoneLocationAsync(serial, latitude, longitude, refresh.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e) when (e is InvalidOperationException or TimeoutException)
                {
                    // Unplugged, or asleep for a moment: keep trying, it reports the place again once it's back.
                }
            }
        });
    }

    private void StopRefreshing()
    {
        _refresh?.Cancel();
        _refresh = null;
    }

    private async Task ClearPhoneAsync()
    {
        StopRefreshing();
        if (_phone is not { } phone) return;

        await _tools.ClearPhoneLocationAsync(phone.Serial);
        _phone = null;
        HasVirtualPhoneLocation = false;
    }

    private static string Format(double latitude, double longitude) =>
        string.Create(CultureInfo.InvariantCulture, $"{latitude:0.######}, {longitude:0.######}");

    /// <summary>Reads "3.1390, 101.6869" or "3.1390 101.6869": latitude first, with a full stop for the decimals.</summary>
    public static bool TryParse(string text, out double latitude, out double longitude)
    {
        latitude = longitude = 0;
        var parts = text.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
               && double.TryParse(parts[0].Trim('°'), NumberStyles.Float, CultureInfo.InvariantCulture, out latitude)
               && double.TryParse(parts[1].Trim('°'), NumberStyles.Float, CultureInfo.InvariantCulture, out longitude)
               && latitude is >= -90 and <= 90
               && longitude is >= -180 and <= 180;
    }
}

/// <summary>One of a place search's matches, in the Location flyout's list; the one on the map is chosen.</summary>
public partial class PlaceChoice(Place place) : ObservableObject
{
    public Place Place { get; } = place;

    [ObservableProperty] private bool _isChosen;
}
