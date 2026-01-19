using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Helpers;
using Launcher.Models;
using Launcher.Services;
using NLog;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Launcher.ViewModels;

public partial class Login : Popup
{
    private readonly Server _server;
    private readonly Logger _logger = LogManager.GetCurrentClassLogger();

    [ObservableProperty] private string? warning;
    [Required] [ObservableProperty] [NotifyDataErrorInfo] private string username = string.Empty;
    [Required] [ObservableProperty] [NotifyDataErrorInfo] private string password = string.Empty;
    [ObservableProperty] private bool rememberUsername;
    [ObservableProperty] private bool rememberPassword;

    public bool AutoFocusUsername => string.IsNullOrEmpty(Username);
    public bool AutoFocusPassword => !string.IsNullOrEmpty(Username) && string.IsNullOrEmpty(Password);
    public IAsyncRelayCommand LoginCommand { get; }
    public ICommand LoginCancelCommand { get; }

    public Login(Server server)
    {
        _server = server;
        AddSecureWarning();
        RememberUsername = _server.Info.RememberUsername;
        RememberPassword = _server.Info.RememberPassword;
        Username = RememberUsername ? _server.Info.Username ?? string.Empty : string.Empty;
        Password = RememberPassword ? _server.Info.Password ?? string.Empty : string.Empty;
        LoginCommand = new AsyncRelayCommand(OnLogin);
        LoginCancelCommand = new RelayCommand(OnLoginCancel);
        View = new Views.Login { DataContext = this };
    }

    private Task OnLogin() => App.ProcessPopupAsync();
    private void OnLoginCancel() => App.CancelPopup();

    private void AddSecureWarning()
    {
        if (Uri.TryCreate(_server.Info.LoginApiUrl, UriKind.Absolute, out var loginApiUrl) && loginApiUrl.Scheme != Uri.UriSchemeHttps)
            Warning = App.GetText("Text.Login.SecureApiWarning");
    }

    public override async Task<bool> ProcessAsync()
    {
        _server.Info.RememberUsername = RememberUsername;
        _server.Info.RememberPassword = RememberPassword;
        _server.Info.Username = RememberUsername ? Username : null;
        _server.Info.Password = RememberPassword ? Password : null;

        Settings.Instance.Save();

        try
        {
            using var client = HttpHelper.CreateHttpClient();
            var resp = await client.PostAsJsonAsync(_server.Info.LoginApiUrl, new LoginRequest { Username = Username, Password = Password });

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                await App.AddNotification(App.GetText("Text.Login.Unauthorized"), true);
                Password = string.Empty;
                return false;
            }
            if (!resp.IsSuccessStatusCode)
            {
                await App.AddNotification($"Login Failed: {resp.ReasonPhrase}", true);
                return false;
            }

            var data = await resp.Content.ReadFromJsonAsync<LoginResponse>();
            if (data == null || string.IsNullOrEmpty(data.SessionId))
            {
                await App.AddNotification("Invalid API response.", true);
                return false;
            }

            await LaunchClientAsync(data.SessionId, data.LaunchArguments);
            return true;
        }
        catch (Exception ex) { await App.AddNotification($"Error: {ex.Message}", true); return false; }
    }

    private async Task LaunchClientAsync(string sessionId, string? serverArguments)
    {
        var workingDir = Path.Combine(Constants.SavePath, _server.Info.SavePath, "Client");
        var exePath = Path.Combine(workingDir, Constants.ClientExecutableName);
        if (!File.Exists(exePath)) { await App.AddNotification($"Client missing: {exePath}", true); return; }

        var args = new List<string> { $"Server={_server.Info.LoginServer}", $"SessionId={sessionId}", $"Internationalization:Locale={Settings.Instance.Locale}" };
        if (!string.IsNullOrEmpty(serverArguments)) args.Add(serverArguments);
        var argsStr = string.Join(' ', args);

        _server.Process = new Process();
        _server.Process.StartInfo.WorkingDirectory = workingDir;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _server.Process.StartInfo.FileName = exePath;
            _server.Process.StartInfo.Arguments = argsStr;
            _server.Process.StartInfo.UseShellExecute = true;
        }
        else // macOS and Linux Logic
        {
            string bin = WineSetupService.EngineBin;
            if (!File.Exists(bin)) { await App.AddNotification("Wine Engine missing. Please restart.", true); return; }

            _server.Process.StartInfo.FileName = bin;
            // Using full path (\"{exePath}\") prevents Wine from getting lost if the working dir is subtlely different
            _server.Process.StartInfo.Arguments = $"\"{exePath}\" {argsStr}";
            _server.Process.StartInfo.UseShellExecute = false;

            // --- Shared Bottle Environment ---
            var env = _server.Process.StartInfo.EnvironmentVariables;
            env["WINEPREFIX"] = WineSetupService.PrefixPath;
            env["WINE_LARGE_ADDRESS_AWARE"] = "1";
            env["WINEDLLOVERRIDES"] = "d3d9=b;d3dx9_31=n,b";
            env["WINE_NOCRASHDIALOG"] = "1";
            env["WINEDEBUG"] = "-all";

            // --- Linux Specific Optimizations ---
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // 1. THE CRASH FIX: Forces the game to use the DXVK dlls we installed instead of Wine's broken built-in ones.
                // "n,b" means "Native first, then Builtin". Without this, you get the Null Pointer crash.
                env["WINEDLLOVERRIDES"] = "d3d9,dxgi=n,b";

                // 2. UNIVERSAL CACHING (Nvidia, AMD, Intel):
                // Tells DXVK to save its state cache in your bottle folder.
                // This prevents stuttering on ALL GPUs by remembering compiled shaders.
                env["DXVK_STATE_CACHE_PATH"] = WineSetupService.WineRootPath;
                env["DXVK_LOG_LEVEL"] = "info";
            }

            // --- macOS Specific Libs ---
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string wineBase = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(bin))) ?? string.Empty;
                string libPath = Path.Combine(wineBase, "lib");
                if (Directory.Exists(libPath)) env["DYLD_LIBRARY_PATH"] = libPath;
            }
        }

        _server.Process.EnableRaisingEvents = true;
        _server.Process.Exited += _server.ClientProcessExited;
        _server.Process.Start();
    }
}
