using CommunityToolkit.Mvvm.ComponentModel;
using DeadheimLauncher.Models;

namespace DeadheimLauncher.ViewModels;

/// <summary>Um mod na lista da UI: dados do manifest + estado de habilitado/instalação do perfil atual.</summary>
public sealed partial class ModListItemViewModel : ObservableObject
{
    public ModEntry Entry { get; }

    public string Name => Entry.Name;
    public string Description => Entry.Description;
    public bool IsRequired => Entry.Required;

    public string SourceLabel => Entry.Source == ModSource.GitHub ? "Mod do servidor" : "Thunderstore";

    /// <summary>Versão fixada pelo pack, ou "mais recente" quando o manifest não pina.</summary>
    public string VersionLabel =>
        string.IsNullOrWhiteSpace(Entry.Version) ? "mais recente" : "v" + Entry.Version;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _statusText = "";

    public ModListItemViewModel(ModEntry entry, bool isEnabled)
    {
        Entry = entry;
        _isEnabled = isEnabled || entry.Required;
    }
}
