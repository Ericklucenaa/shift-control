using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShiftControl.App.Services;
using ShiftControl.Hardware;
using ShiftControl.Storage;

namespace ShiftControl.App.ViewModels;

public sealed partial class ChangeVm : ObservableObject
{
    public ChangeVm(ChangeEntry e) => Entry = e;

    public ChangeEntry Entry { get; }
    public string Title => Entry.Title;
    public string AreaText => L.Get("undo.area." + Entry.Area) is var t && t != "undo.area." + Entry.Area ? t : Entry.Area;
    public string When => Fmt.When(Entry.At, DateTimeOffset.Now);
    public string StateText => L.Get("undo.state." + Entry.State);
    public string Result => Entry.Result ?? "";
    public bool HasResult => !string.IsNullOrEmpty(Entry.Result);
    public bool CanRevert => Entry.CanRevert;

    [ObservableProperty] public partial bool Busy { get; set; }
}

public sealed partial class UndoViewModel : ObservableObject
{
    private readonly UndoService _undo;

    public UndoViewModel(UndoService undo)
    {
        _undo = undo;
        undo.Journal.Changed += (_, _) => Dirty = true;
    }

    public ObservableCollection<ChangeVm> Changes { get; } = [];

    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial bool MessageIsError { get; set; }
    [ObservableProperty] public partial bool CreatingPoint { get; set; }
    [ObservableProperty] public partial bool IsElevated { get; set; } = Elevation.IsElevated;

    public bool Dirty { get; private set; } = true;

    public async Task LoadAsync()
    {
        var list = await Task.Run(() => _undo.Journal.List());
        Changes.Clear();
        foreach (var e in list) Changes.Add(new ChangeVm(e));
        IsEmpty = Changes.Count == 0;
        Dirty = false;
    }

    /// <summary>Chamado pela tela depois da confirmação do usuário.</summary>
    public async Task RevertAsync(ChangeVm change)
    {
        change.Busy = true;
        var (ok, message) = await _undo.RevertAsync(change.Entry);
        Show(ok ? L.F("undo.reverted", change.Title, message) : L.F("undo.revertFailed", change.Title, message), !ok);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task CreateRestorePointAsync()
    {
        CreatingPoint = true;
        var result = await Task.Run(() => RestorePoints.Create("Shift Control"));
        CreatingPoint = false;
        if (result == RestorePointResult.Created)
            _undo.Journal.Record("system", L.Get("undo.restorePointTitle"), null, null, null);
        Show(L.Get("undo.restorePoint." + result), result != RestorePointResult.Created);
        await LoadAsync();
    }

    private void Show(string text, bool error)
    {
        MessageIsError = error;
        Message = null;
        Message = text;
    }
}
