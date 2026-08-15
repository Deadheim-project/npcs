using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeadheimLauncher.Models;
using DeadheimLauncher.Services;

namespace DeadheimLauncher.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly HttpClient _http = new();
    private readonly SettingsService _settingsService = new();
    private readonly ProfileService _profileService = new();
    private readonly ManifestService _manifestService;
    private readonly ModInstallerService _installerService;
    private readonly ValheimLaunchService _launchService = new();

    private LauncherSettings _settings = new();
    private ModManifest _manifest = new();
    private Profile _activeProfile = new();

    public ObservableCollection<string> Profiles { get; } = new();
    public ObservableCollection<ModListItemViewModel> Mods { get; } = new();

    [ObservableProperty]
    private string? _selectedProfile;

    [ObservableProperty]
    private string _statusText = "Iniciando...";

    [ObservableProperty]
    private bool _isBusy;

    public MainViewModel()
    {
        _manifestService = new ManifestService(_http);
        _installerService = new ModInstallerService(_http, new GitHubReleaseService(_http), new ThunderstoreService(_http));
    }

    public async Task InitializeAsync()
    {
        IsBusy = true;
        StatusText = "Carregando configurações...";
        try
        {
            _settings = _settingsService.Load();

            var profiles = _profileService.ListProfiles();
            if (profiles.Count == 0)
            {
                _profileService.LoadOrCreate("Default");
                profiles = _profileService.ListProfiles();
            }

            Profiles.Clear();
            foreach (var p in profiles) Profiles.Add(p);

            var startProfile = profiles.Contains(_settings.LastActiveProfile) ? _settings.LastActiveProfile : profiles[0];

            StatusText = "Baixando lista de mods do servidor...";
            _manifest = await _manifestService.GetManifestAsync(_settings.ManifestUrl);

            await SwitchProfileAsync(startProfile);
            StatusText = "Pronto.";
        }
        catch (Exception ex)
        {
            StatusText = $"Erro ao iniciar: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedProfileChanged(string? value)
    {
        if (value is not null && value != _activeProfile.Name)
        {
            _ = SwitchProfileAsync(value);
        }
    }

    private async Task SwitchProfileAsync(string profileName)
    {
        _activeProfile = _profileService.LoadOrCreate(profileName);
        SelectedProfile = profileName;
        _settings.LastActiveProfile = profileName;
        _settingsService.Save(_settings);

        Mods.Clear();
        foreach (var entry in _manifest.AllMods)
        {
            var enabled = entry.Required || _activeProfile.EnabledModIds.Contains(entry.Id);
            Mods.Add(new ModListItemViewModel(entry, enabled));
        }

        await Task.CompletedTask;
    }

    [RelayCommand]
    private void CreateProfile()
    {
        var name = PromptForName("Nome do novo perfil:", "Novo Perfil");
        if (string.IsNullOrWhiteSpace(name) || Profiles.Contains(name)) return;

        _profileService.LoadOrCreate(name);
        Profiles.Add(name);
        SelectedProfile = name;
    }

    [RelayCommand]
    private void DuplicateProfile()
    {
        if (SelectedProfile is null) return;
        var name = PromptForName("Nome do perfil duplicado:", $"{SelectedProfile} - cópia");
        if (string.IsNullOrWhiteSpace(name) || Profiles.Contains(name)) return;

        _profileService.Duplicate(_activeProfile, name);
        Profiles.Add(name);
        SelectedProfile = name;
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile is null || Profiles.Count <= 1) return;
        var toDelete = SelectedProfile;

        var result = MessageBox.Show($"Excluir o perfil '{toDelete}'? Essa ação não pode ser desfeita.",
            "Confirmar exclusão", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _profileService.Delete(toDelete);
        Profiles.Remove(toDelete);
        SelectedProfile = Profiles[0];
    }

    [RelayCommand]
    private async Task RefreshManifestAsync()
    {
        IsBusy = true;
        StatusText = "Atualizando lista de mods...";
        try
        {
            _manifest = await _manifestService.GetManifestAsync(_settings.ManifestUrl);
            await SwitchProfileAsync(_activeProfile.Name);
            StatusText = "Lista de mods atualizada.";
        }
        catch (Exception ex)
        {
            StatusText = $"Erro ao atualizar lista: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        IsBusy = true;
        try
        {
            await InstallEnabledModsAsync();

            StatusText = "Sincronizando mods com o Valheim...";
            var valheimPath = _launchService.ResolveValheimPath(_settings);
            _launchService.SyncProfileToGame(valheimPath, _activeProfile.Name);

            StatusText = "Iniciando o Valheim...";
            _launchService.LaunchGame();
            StatusText = "Valheim iniciado.";
        }
        catch (Exception ex)
        {
            StatusText = $"Erro: {ex.Message}";
            MessageBox.Show(ex.Message, "Não foi possível iniciar", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallEnabledModsAsync()
    {
        // Persiste quais mods ficaram habilitados/desabilitados neste perfil.
        _activeProfile.EnabledModIds = Mods.Where(m => m.IsEnabled).Select(m => m.Entry.Id).ToList();
        _profileService.Save(_activeProfile);

        foreach (var modVm in Mods.Where(m => m.IsEnabled))
        {
            var progress = new Progress<ModInstallProgress>(p => modVm.StatusText = p.Status);
            try
            {
                var installedVersion = await _installerService.InstallAsync(modVm.Entry, _activeProfile.Name, progress);
                _activeProfile.InstalledVersions[modVm.Entry.Id] = installedVersion;
                _profileService.Save(_activeProfile);
            }
            catch (Exception ex)
            {
                modVm.StatusText = $"Falhou: {ex.Message}";
            }
        }

        // Remove do disco mods que foram desabilitados neste perfil.
        foreach (var modVm in Mods.Where(m => !m.IsEnabled))
        {
            var dir = Path.Combine(AppPaths.ProfilePluginsDir(_activeProfile.Name), modVm.Entry.Id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            _activeProfile.InstalledVersions.Remove(modVm.Entry.Id);
        }
        _profileService.Save(_activeProfile);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var window = new Views.SettingsWindow(_settings, _settingsService)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
        _settings = _settingsService.Load();
    }

    private static string? PromptForName(string message, string defaultValue)
    {
        var dialog = new Views.InputDialog(message, defaultValue) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.ResponseText : null;
    }
}
