using Launcher.Helpers;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Launcher.Services;

public static class WineSetupService
{
    private const string MacEngineUrl = "https://github.com/Gcenx/winecx/releases/download/crossover-wine-23.7.1-1/wine-crossover-23.7.1-1-osx64.tar.xz";

        // Updated to Wine 11.0-rc3 WoW64 for best 2026 compatibility
        private const string LinuxEngineUrl = "https://github.com/Kron4ek/Wine-Builds/releases/download/11.0-rc3/wine-11.0-rc3-amd64-wow64.tar.xz";
            private const string LinuxDxvkUrl = "https://github.com/doitsujin/dxvk/releases/download/v2.3/dxvk-2.3.tar.gz";

                public static string WineRootPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                                                  RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "Library/Application Support/OSFRLauncher/WineSystem"
                                                                  : ".local/share/OSFRLauncher/WineSystem");

                public static string EngineBin => RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? Path.Combine(WineRootPath, "Wine Crossover.app", "Contents", "Resources", "wine", "bin", "wine64")
                : Path.Combine(WineRootPath, "wine-11.0-rc3-amd64-wow64", "bin", "wine");

                public static string PrefixPath => Path.Combine(WineRootPath, "Prefix");

                public static bool IsInstalled() => File.Exists(EngineBin) && Directory.Exists(PrefixPath);

                public static async Task Install(IProgress<string> status, IProgress<double> progress)
                {
                    if (!Directory.Exists(WineRootPath)) Directory.CreateDirectory(WineRootPath);

                    // 1. Download and Extract the Wine Engine
                    if (!File.Exists(EngineBin))
                    {
                        status.Report("Downloading Game Engine...");
                        string url = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? MacEngineUrl : LinuxEngineUrl;
                        string archivePath = Path.Combine(WineRootPath, "engine.tar.xz");

                        await DownloadFileAsync(url, archivePath, progress);
                        status.Report("Installing Engine...");
                        await ExtractNative(archivePath, WineRootPath);
                        if (File.Exists(archivePath)) File.Delete(archivePath);
                    }

                    // 2. Setup the Bottle (WINEPREFIX)
                    if (!Directory.Exists(PrefixPath))
                    {
                        status.Report("Building Bottle...");
                        await RunWineCommand("wineboot", "-u");

                        // Disable Mono/Gecko popups
                        await RunWineCommand("reg", "add HKCU\\Software\\Wine\\DllOverrides /v mscoree /t REG_SZ /d \"\" /f");
                        await RunWineCommand("reg", "add HKCU\\Software\\Wine\\DllOverrides /v mshtml /t REG_SZ /d \"\" /f");
                        
                        // --- NEW: Set Windows Version to Windows XP ---
    // This tells Wine to report as "winxp". For better stability with older engines.
    await RunWineCommand("reg", "add HKCU\\Software\\Wine /v Version /t REG_SZ /d winxp /f");

                        // Disable Wine's built-in menu builder to avoid "winemenubuilder.exe not found" errors in logs
                        await RunWineCommand("reg", "add HKCU\\Software\\Wine\\DllOverrides /v winemenubuilder.exe /t REG_SZ /d \"\" /f");
                        
                        // Force the engine to use the correct version of the DirectX helper
await RunWineCommand("reg", "add \"HKCU\\Software\\Wine\\DllOverrides\" /v \"d3dx9_31,d3dx9_36,d3d9\" /t REG_SZ /d native,builtin /f");
                        
                        // Apply Visual C++ 2005 (msvcr80) Compatibility
                            status.Report("Applying Visual C++ 2005 Compatibility...");
                            // native,builtin forces Wine to use the Microsoft DLL if present in the game folder
                            await RunWineCommand("reg", "add \"HKCU\\Software\\Wine\\DllOverrides\" /v msvcr80 /t REG_SZ /d native,builtin /f");
                            await RunWineCommand("reg", "add \"HKCU\\Software\\Wine\\DllOverrides\" /v msvcp80 /t REG_SZ /d native,builtin /f");

                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                        {
                            // Install DXVK (D3D9 to Vulkan)
                            await InstallDxvk(status);

                        }
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                        {
                            // --- NEW: Disable CSMT (Multi-threaded rendering) ---
// This prevents the graphics engine from "racing" ahead of the game logic, 
// which causes the 0x180 Null Pointer crash.
await RunWineCommand("reg", "add HKCU\\Software\\Wine\\Direct3D /v CSMT /t REG_DWORD /d 0x0 /f");

                        }
                    }
                    status.Report("Setup Complete!");
                }

                private static async Task InstallDxvk(IProgress<string> status)
                {
                    status.Report("Installing DXVK (Vulkan)...");
                    string archivePath = Path.Combine(WineRootPath, "dxvk.tar.gz");
                    string extractPath = Path.Combine(WineRootPath, "dxvk_temp");

                    try
                    {
                        await DownloadFileAsync(LinuxDxvkUrl, archivePath, null);
                        if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                        Directory.CreateDirectory(extractPath);
                        await ExtractNative(archivePath, extractPath);

                        string dxvkRoot = Directory.GetDirectories(extractPath)[0];
                        string x32Src = Path.Combine(dxvkRoot, "x32");

                        // In WoW64, we populate both for absolute safety, though syswow64 is the primary 32-bit target
                        string sys32 = Path.Combine(PrefixPath, "drive_c", "windows", "system32");
                        string syswow64 = Path.Combine(PrefixPath, "drive_c", "windows", "syswow64");

                        if (!Directory.Exists(sys32)) Directory.CreateDirectory(sys32);
                        if (!Directory.Exists(syswow64)) Directory.CreateDirectory(syswow64);

                        string[] files = { "d3d9.dll", "dxgi.dll" };
                        foreach (var file in files)
                        {
                            File.Copy(Path.Combine(x32Src, file), Path.Combine(sys32, file), true);
                            File.Copy(Path.Combine(x32Src, file), Path.Combine(syswow64, file), true);
                        }
                    }
                    catch (Exception ex) { Debug.WriteLine($"DXVK Fail: {ex.Message}"); }
                    finally
                    {
                        if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                        if (File.Exists(archivePath)) File.Delete(archivePath);
                    }
                }

                public static async Task RunWineCommand(string cmd, string args)
                {
                    var psi = new ProcessStartInfo(EngineBin) { UseShellExecute = false, CreateNoWindow = true };
                    psi.Arguments = $"{cmd} {args}";
                    psi.EnvironmentVariables["WINEPREFIX"] = PrefixPath;
                    psi.EnvironmentVariables["WINE_LARGE_ADDRESS_AWARE"] = "1";
                    psi.EnvironmentVariables["WINEDEBUG"] = "-all";
                    using var p = Process.Start(psi);
                    if (p != null) await p.WaitForExitAsync();
                }

                private static async Task DownloadFileAsync(string url, string dest, IProgress<double>? progress)
                {
                    using var client = new HttpClient();
                    using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    var total = resp.Content.Headers.ContentLength ?? -1L;
                    using var s = await resp.Content.ReadAsStreamAsync();
                    using var f = new FileStream(dest, FileMode.Create);
                    var buf = new byte[8192]; long readTotal = 0; int read;
                    while ((read = await s.ReadAsync(buf)) > 0)
                    {
                        await f.WriteAsync(buf, 0, read);
                        readTotal += read;
                        if (progress != null && total != -1) progress.Report((double)readTotal / total * 100);
                    }
                }

                private static async Task ExtractNative(string archivePath, string outputDir)
                {
                    var psi = new ProcessStartInfo("tar") { UseShellExecute = false, CreateNoWindow = true };
                    psi.ArgumentList.Add("-xf");
                    psi.ArgumentList.Add(archivePath);
                    psi.ArgumentList.Add("-C");
                    psi.ArgumentList.Add(outputDir);
                    using var p = Process.Start(psi);
                    if (p != null) await p.WaitForExitAsync();
                }
}