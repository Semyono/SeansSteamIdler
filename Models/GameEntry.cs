using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SeansSteamIdler.Models;

public class GameEntry : INotifyPropertyChanged
{
    public uint AppId { get; set; }
    public string Name { get; set; } = "";
    public double PlaytimeForeverHours { get; set; }
    private bool _isSelected;
    private bool _isFavorite;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FavoriteGlyph));
        }
    }

    public string HeaderImageUrl => $"https://cdn.cloudflare.steamstatic.com/steam/apps/{AppId}/header.jpg";
    public string FormattedPlaytime => PlaytimeForeverHours > 0
        ? $"{PlaytimeForeverHours:0.#} hrs played"
        : "Playtime hidden";
    public string DisplayName => Name;
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
