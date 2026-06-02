using System;
using System.ComponentModel;
using MgsvModMgr.Core;

namespace MgsvModMgr.Gui;

/// <summary>
/// View-model wrapper around a single <see cref="ModInfo"/>.
/// The underlying <see cref="ModInfo"/> is owned by <see cref="ModManager"/>;
/// this object only adapts it for binding and notifies the host on toggle.
/// </summary>
public sealed class ModRow : INotifyPropertyChanged
{
    private readonly ModInfo _mod;
    private readonly Action<bool> _onEnabledChanged;

    public ModRow(ModInfo mod, Action<bool> onEnabledChanged)
    {
        _mod              = mod;
        _onEnabledChanged = onEnabledChanged;
    }

    public string Id           => _mod.Id;
    public string Name         => string.IsNullOrEmpty(_mod.Name) ? _mod.Id : _mod.Name;
    public string Version      => _mod.Version;
    public string Author       => _mod.Author;
    public int    QarCount     => _mod.QarPaths.Count;
    public int    GameDirCount => _mod.GameDirEntries.Count;

    public bool Enabled
    {
        get => _mod.Enabled;
        set
        {
            if (_mod.Enabled == value) return;
            _mod.Enabled = value;
            _onEnabledChanged(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
