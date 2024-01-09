﻿using System.Text.RegularExpressions;
using Microsoft.Graph.Beta.Models;
using Riok.Mapperly.Abstractions;
using WingetIntune.Internal.MsStore;
using WingetIntune.Intune;

namespace WingetIntune.Models;

[Mapper]
internal partial class Mapper
{
    public Win32LobApp ToWin32LobApp(PackageInfo packageInfo)
    {
        if (packageInfo is null)
        {
            throw new ArgumentNullException(nameof(packageInfo));
        }

        if (packageInfo.Source != PackageSource.Winget)
        {
            throw new NotSupportedException($"Package source {packageInfo.Source} is not supported");
        }

        var app = _ToWin32LobApp(packageInfo);
        app.DisplayVersion = packageInfo.Version;
        app.InstallExperience = new Win32LobAppInstallExperience()
        {
            RunAsAccount = packageInfo.InstallerContext == InstallerContext.User ? RunAsAccountType.User : RunAsAccountType.System,
            DeviceRestartBehavior = Win32LobAppRestartBehavior.BasedOnReturnCode
        };
        app.AllowAvailableUninstall = true;

        // This version of windows has the ability to install winget packages, if I'm not mistaken
        app.MinimumSupportedWindowsRelease = "2004";
        app.MinimumSupportedOperatingSystem = new WindowsMinimumOperatingSystem
        {
            V102004 = true
        };

        app.ReturnCodes = new List<Win32LobAppReturnCode>
            {
                new Win32LobAppReturnCode { Type = Win32LobAppReturnCodeType.Success, ReturnCode = 0 },
                new Win32LobAppReturnCode { Type = Win32LobAppReturnCodeType.Success, ReturnCode = 1707 },
                new Win32LobAppReturnCode { Type = Win32LobAppReturnCodeType.SoftReboot, ReturnCode = 3010 },
                new Win32LobAppReturnCode { Type = Win32LobAppReturnCodeType.HardReboot, ReturnCode = 1641 },
                new Win32LobAppReturnCode { Type = Win32LobAppReturnCodeType.Retry, ReturnCode = 1618 }
        };

        app.ApplicableArchitectures = ToGraphArchitecture(packageInfo.Architecture);

        if (packageInfo.InstallerType.IsMsi())
        {
            app.MsiInformation = new Win32LobAppMsiInformation
            {
                ProductCode = packageInfo.MsiProductCode!,
                ProductVersion = packageInfo.MsiVersion!,
                Publisher = packageInfo.Publisher,
                ProductName = packageInfo.DisplayName
            };

            app.DetectionRules = new List<Win32LobAppDetection>
            {
                new Win32LobAppProductCodeDetection
                {
                    ProductCode = packageInfo.MsiProductCode,
                    ProductVersion = packageInfo.MsiVersion,
                    ProductVersionOperator = Win32LobAppDetectionOperator.GreaterThanOrEqual
                }
            };
        }
        else
        {
            app.DetectionRules = new List<Win32LobAppDetection>
            {
                new Win32LobAppPowerShellScriptDetection
                {
                    ScriptContent = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(packageInfo.DetectionScript!)),
                    EnforceSignatureCheck = false,
                    RunAs32Bit = packageInfo.Architecture == Architecture.X86
                }
            };
        }
        app.Developer = packageInfo.Publisher;
        app.FileName = Path.GetFileNameWithoutExtension(packageInfo.InstallerFilename) + ".intunewin";
        app.SetupFilePath = packageInfo.InstallerFilename ?? "install.ps1";
        app.Notes = $"Generated by {nameof(WingetIntune)} at {DateTimeOffset.UtcNow} [WingetIntune|{packageInfo.Source}|{packageInfo.PackageIdentifier}]";
        return app;
    }

    private partial Win32LobApp _ToWin32LobApp(PackageInfo packageInfo);

    private static WindowsArchitecture ToGraphArchitecture(Architecture? architecture) => architecture switch
    {
        Architecture.Arm64 => WindowsArchitecture.Arm64,
        Architecture.X86 => WindowsArchitecture.X86 | WindowsArchitecture.X64,
        Architecture.X64 => WindowsArchitecture.X64,
        _ => WindowsArchitecture.Neutral
    };

    public WinGetApp ToWinGetApp(MicrosoftStoreManifest storeManifest)
    {
        var locale = storeManifest.Data.Versions.LastOrDefault()?.DefaultLocale!;
        var app = _ToWinGetApp(locale);
        app.DisplayName = locale.PackageName;
        app.PackageIdentifier = storeManifest.Data.PackageIdentifier;
        app.InformationUrl = locale.PublisherSupportUrl?.ValidUriOrNull();
        app.PrivacyInformationUrl = locale.PrivacyUrl?.ValidUriOrNull();
        app.AdditionalData.Add("repositoryType", "microsoftstore");
        app.InstallExperience = new WinGetAppInstallExperience()
        {
            RunAsAccount = storeManifest.Data.Versions?.LastOrDefault()?.Installers?.LastOrDefault()?.Scope == "user" ? RunAsAccountType.User : RunAsAccountType.System,
        };
        app.Developer = app.Publisher;
        app.Description ??= locale.ShortDescription;
        app.Notes = $"Generated by {nameof(WingetIntune)} at {DateTimeOffset.UtcNow} [WingetIntune|store|{storeManifest.Data.PackageIdentifier}]";
        return app;
    }

    private partial WinGetApp _ToWinGetApp(MicrosoftStoreManifestDefaultlocale locale);

    internal partial WingetIntune.Graph.FileEncryptionInfo ToFileEncryptionInfo(ApplicationInfoEncryptionInfo packageInfo);

    internal static IntuneApp ToIntuneApp(Win32LobApp? win32LobApp) {
        ArgumentNullException.ThrowIfNull(win32LobApp, nameof(win32LobApp));

        var (packageId, source) = win32LobApp.Notes.ExtractPackageIdAndSourceFromNotes();
        return new IntuneApp {
            PackageId = packageId!,
            Name = win32LobApp.DisplayName!,
            Version = win32LobApp.DisplayVersion!,
            GraphId = win32LobApp.Id!
        };
    }
}

internal static class MapperExtensions
{
    public static string? ValidUriOrNull(this string? input)
        => Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http") ? uri.ToString() : null;
}

internal static class StringExtensions
{
    internal static (string?, string?) ExtractPackageIdAndSourceFromNotes(this string? notes) {
        if (notes is null || !notes.Contains("[WingetIntune|"))
        {
            return (null, null);
        }

        var match = Regex.Match(notes, @"\[WingetIntune\|(?<source>[^\|]+)\|(?<packageId>[^\]]+)\]");
        if (match.Success)
        {
            return (match.Groups["packageId"].Value, match.Groups["source"].Value);
        }
        else
        {
            return (null, null);
        }
    }
}
