// ════════════════════════════════════════════════════════════════════════════
// BloatwareService.cs  ·  Detects and removes pre-installed / OEM apps
// ════════════════════════════════════════════════════════════════════════════
//
// Maintains a curated catalogue of safe-to-remove apps in two categories:
//
//   UWP   — Microsoft Store / AppxPackage apps, detected via Get-AppxPackage.
//   Win32 — Traditional installer apps, detected via registry uninstall keys
//            and removed via winget (with UninstallString as fallback).
//
// SAFETY RULES:
//   ✗  No package required for Xbox Live or the Microsoft Store
//   ✗  No package Windows itself depends on (VCLibs, UI.Xaml, etc.)
//   ✗  No productivity apps people commonly use (Calculator, Photos, Paint)
//   ✗  No critical OEM drivers (audio, GPU, NIC) — only companion/bloat apps
//
// RELATED FILES
//   Models/BloatwareEntry.cs    — data shape for a single detectable app
//   BloatwareViewModel.cs       — drives the App Cleanup tab
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using Microsoft.Win32;
using Systema.Core;
using Systema.Models;

namespace Systema.Services;

public class BloatwareService
{
    private static readonly LoggerService _log = LoggerService.Instance;

    private enum EntryKind { Uwp, Win32 }

    // Identifiers:
    //   UWP   → AppxPackage names (any match = installed)
    //   Win32 → Registry DisplayName substrings to match (case-insensitive)
    // WingetId → Win32 removal via winget (null = use UninstallString only)
    private record CatalogueEntry(
        EntryKind Kind,
        string[]  Identifiers,
        string    DisplayName,
        string    Description,
        string?   WingetId = null);

    private static readonly CatalogueEntry[] _catalogue =
    [
        // ═══════════════════════════════════════════════════════════════════
        // UWP APPS  (Microsoft Store / AppxPackage)
        // ═══════════════════════════════════════════════════════════════════

        // ── Microsoft Bing / info apps ───────────────────────────────────
        new(EntryKind.Uwp, ["Microsoft.BingNews"],
            "Bing News",
            "Microsoft's news feed app. Easy to replace with any news website."),

        new(EntryKind.Uwp, ["Microsoft.BingWeather"],
            "Bing Weather",
            "Microsoft's weather app. Not needed if you check weather in a browser."),

        new(EntryKind.Uwp, ["Microsoft.BingSearch"],
            "Bing Search",
            "A dedicated Bing search app. Your browser already does this."),

        new(EntryKind.Uwp, ["Microsoft.BingFinance"],
            "Bing Finance",
            "A stock/finance tracking app from the Windows 8 era. Rarely used today."),

        new(EntryKind.Uwp, ["Microsoft.BingSports"],
            "Bing Sports",
            "A sports scores app from the Windows 8 era. Easy to replace with any sports site."),

        new(EntryKind.Uwp, ["Microsoft.BingTravel"],
            "Bing Travel",
            "A travel planning app from the Windows 8 era. Safe to remove."),

        new(EntryKind.Uwp, ["Microsoft.BingFoodAndDrink"],
            "Bing Food & Drink",
            "A recipe app from the Windows 8 era. Safe to remove."),

        new(EntryKind.Uwp, ["Microsoft.BingHealthAndFitness"],
            "Bing Health & Fitness",
            "A health tracking app from the Windows 8 era. Safe to remove."),

        // ── Communication / productivity ─────────────────────────────────
        new(EntryKind.Uwp, ["Microsoft.SkypeApp"],
            "Skype (UWP)",
            "Pre-installed Skype. If you don't use Skype, this can be safely removed."),

        new(EntryKind.Uwp, ["MSTeams"],
            "Microsoft Teams (pre-installed)",
            "The consumer version of Teams pre-pinned by Windows. Removable if unused."),

        new(EntryKind.Uwp, ["Microsoft.OutlookForWindows"],
            "New Outlook",
            "Microsoft's new Outlook app. Remove if you use another email client."),

        new(EntryKind.Uwp, ["Microsoft.windowscommunicationsapps"],
            "Mail & Calendar",
            "The built-in Mail and Calendar apps. Remove if you use Gmail or another client."),

        new(EntryKind.Uwp, ["Microsoft.People"],
            "People",
            "A contacts app. Not needed if you manage contacts through your browser or email app."),

        new(EntryKind.Uwp, ["Microsoft.Messaging"],
            "Messaging",
            "An SMS relay app tied to old Cortana features. No longer functional on most systems."),

        new(EntryKind.Uwp, ["Microsoft.Todos"],
            "Microsoft To Do",
            "Microsoft's task-list app. Remove if you use a different app or don't need it."),

        new(EntryKind.Uwp, ["Microsoft.MicrosoftOfficeHub"],
            "Office Hub",
            "A launcher tile for Microsoft Office subscriptions. Not the Office apps themselves."),

        new(EntryKind.Uwp, ["Microsoft.Office.OneNote"],
            "OneNote (Store version)",
            "The UWP Store version of OneNote. The full desktop OneNote won't be affected."),

        new(EntryKind.Uwp, ["Microsoft.PowerAutomateDesktop"],
            "Power Automate",
            "An advanced automation tool for power users. Safe to remove if unused."),

        // ── Gaming / Xbox ─────────────────────────────────────────────────
        new(EntryKind.Uwp, ["Microsoft.MicrosoftSolitaireCollection"],
            "Solitaire Collection",
            "Pre-installed card games. Reinstallable from the Store anytime."),

        new(EntryKind.Uwp, ["Microsoft.MicrosoftMahjong"],
            "Microsoft Mahjong",
            "Pre-installed Mahjong game. Reinstallable from the Microsoft Store."),

        new(EntryKind.Uwp, ["Microsoft.MicrosoftMinesweeper"],
            "Microsoft Minesweeper",
            "Pre-installed Minesweeper. Reinstallable from the Microsoft Store."),

        new(EntryKind.Uwp, ["Microsoft.MicrosoftJigsaw"],
            "Microsoft Jigsaw",
            "Pre-installed jigsaw puzzle game. Reinstallable from the Microsoft Store."),

        new(EntryKind.Uwp, ["Microsoft.MicrosoftSudoku"],
            "Microsoft Sudoku",
            "Pre-installed Sudoku. Reinstallable from the Microsoft Store."),

        new(EntryKind.Uwp, ["Microsoft.XboxApp"],
            "Xbox Console Companion",
            "The older Xbox app for consoles. Replaced by the newer Xbox app."),

        new(EntryKind.Uwp, ["Microsoft.GamingApp"],
            "Xbox App",
            "The Xbox PC app (Game Pass, achievements). Remove only if you don't use Xbox."),

        // ── Third-party games ─────────────────────────────────────────────
        new(EntryKind.Uwp, ["king.com.CandyCrushSaga"],
            "Candy Crush Saga",
            "Pre-installed mobile game. Almost universally considered bloatware."),

        new(EntryKind.Uwp, ["king.com.CandyCrushSodaSaga"],
            "Candy Crush Soda Saga",
            "Pre-installed mobile game. Safe to remove."),

        new(EntryKind.Uwp, ["king.com.BubbleWitch3Saga"],
            "Bubble Witch 3 Saga",
            "Pre-installed mobile game. Safe to remove."),

        new(EntryKind.Uwp, ["king.com.FarmHeroesSaga"],
            "Farm Heroes Saga",
            "Pre-installed mobile game. Safe to remove."),

        new(EntryKind.Uwp, ["Gameloft.AsphaltStreetStormRacing", "Gameloft.Asphalt8Airborne"],
            "Asphalt Racing (Gameloft)",
            "Pre-installed racing game trial. Safe to remove."),

        new(EntryKind.Uwp, ["A278AB0D.MarchofEmpires"],
            "March of Empires",
            "Strategy game pre-installed on some OEM machines. Safe to remove."),

        new(EntryKind.Uwp, ["HiddenCityMysteryofShadows"],
            "Hidden City: Mystery of Shadows",
            "Hidden object game pre-installed on some OEM machines. Safe to remove."),

        // ── Streaming / entertainment ─────────────────────────────────────
        new(EntryKind.Uwp, ["SpotifyAB.SpotifyMusic"],
            "Spotify (pre-installed UWP)",
            "Spotify trial pre-installed by some OEMs. Remove if you installed Spotify yourself."),

        new(EntryKind.Uwp, ["4DF9E0F8.Netflix"],
            "Netflix (UWP)",
            "Netflix app pre-installed on some OEM machines. Works in any browser too."),

        new(EntryKind.Uwp, ["AmazonVideo.PrimeVideo"],
            "Amazon Prime Video",
            "Amazon Prime Video UWP app. Streams through any browser without it."),

        new(EntryKind.Uwp, ["Disney.37853D22215E2"],
            "Disney+",
            "Disney+ streaming app pre-installed by some OEMs. Works in any browser."),

        new(EntryKind.Uwp, ["PandoraMediaInc.29680B314EFC2"],
            "Pandora",
            "Pandora music streaming app pre-installed on some OEM machines. Safe to remove."),

        new(EntryKind.Uwp, ["Microsoft.ZuneVideo"],
            "Movies & TV",
            "Microsoft's video player. VLC is a great free replacement."),

        new(EntryKind.Uwp, ["Microsoft.ZuneMusic"],
            "Groove Music",
            "Microsoft's music player — discontinued. Spotify or similar is recommended."),

        new(EntryKind.Uwp, ["TuneIn.TuneInRadio"],
            "TuneIn Radio",
            "Radio streaming app pre-installed on some machines. Accessible via browser too."),

        // ── Social media ──────────────────────────────────────────────────
        new(EntryKind.Uwp, ["9E2F88E3.Twitter", "Twitter.Twitter"],
            "Twitter / X",
            "Pre-installed Twitter (X) app. The website works fine without it."),

        new(EntryKind.Uwp, ["LinkedIn.LinkedIn"],
            "LinkedIn",
            "LinkedIn app pre-installed on some machines. Accessible via browser."),

        new(EntryKind.Uwp, ["Shazam.Shazam"],
            "Shazam",
            "Music identification app. Pre-installed on some OEM systems. Safe to remove."),

        new(EntryKind.Uwp, ["XINGAG.XING"],
            "XING",
            "A European professional networking app pre-installed on some OEM machines."),

        // ── 3D / creative ─────────────────────────────────────────────────
        new(EntryKind.Uwp, ["Microsoft.Microsoft3DViewer"],
            "3D Viewer",
            "Opens 3D model files. Most users never need this."),

        new(EntryKind.Uwp, ["Microsoft.Print3D"],
            "Print 3D",
            "A tool for sending files to 3D printers. Safe to remove unless you own one."),

        new(EntryKind.Uwp, ["Microsoft.3DBuilder"],
            "3D Builder",
            "An older 3D model creation app. Superseded by newer tools. Safe to remove."),

        new(EntryKind.Uwp, ["Clipchamp.Clipchamp"],
            "Clipchamp",
            "Microsoft's video editor. Safe to remove if you don't edit video."),

        new(EntryKind.Uwp, ["Microsoft.MixedReality.Portal"],
            "Mixed Reality Portal",
            "Only needed for Windows Mixed Reality VR headsets. Safe to remove otherwise."),

        // ── System / utility apps ─────────────────────────────────────────
        new(EntryKind.Uwp, ["Microsoft.549981C3F5F10"],
            "Cortana",
            "Microsoft's voice assistant. Search and Start Menu still work without it."),

        new(EntryKind.Uwp, ["Microsoft.WindowsFeedbackHub"],
            "Feedback Hub",
            "Sends usage feedback to Microsoft. Safe to remove on non-Insider builds."),

        new(EntryKind.Uwp, ["Microsoft.GetHelp"],
            "Get Help",
            "Microsoft's built-in support app. You can find help on the web instead."),

        new(EntryKind.Uwp, ["Microsoft.Getstarted"],
            "Tips",
            "A tutorial app for new Windows users. Safe to remove once you're familiar."),

        new(EntryKind.Uwp, ["Microsoft.YourPhone", "MicrosoftCorporationII.YourPhoneExperience"],
            "Phone Link",
            "Connects an Android phone to Windows. Remove if you don't use this feature."),

        new(EntryKind.Uwp, ["MicrosoftCorporationII.QuickAssist"],
            "Quick Assist",
            "A remote support tool. Only needed if you help others fix their PC remotely."),

        new(EntryKind.Uwp, ["MicrosoftCorporationII.MicrosoftFamily", "Microsoft.Family"],
            "Microsoft Family Safety",
            "Parental controls and family monitoring. Safe to remove if not used."),

        new(EntryKind.Uwp, ["Microsoft.Wallet"],
            "Microsoft Wallet",
            "Microsoft Pay / digital wallet app. Payments work through your browser."),

        new(EntryKind.Uwp, ["Microsoft.WindowsMaps"],
            "Maps",
            "Microsoft's offline maps app. Most people use Google Maps in a browser."),

        new(EntryKind.Uwp, ["Microsoft.WindowsSoundRecorder", "Microsoft.Sound.Recorder"],
            "Voice Recorder",
            "Windows voice/audio recording app. Remove if you use another recording tool."),

        new(EntryKind.Uwp, ["Microsoft.WindowsAlarms"],
            "Alarms & Clock",
            "The Windows Alarms and Clock app. Remove if you don't use PC-based alarms."),

        // ── OEM UWP companion apps ────────────────────────────────────────
        new(EntryKind.Uwp, ["AD2F1837.HPSupportAssistant"],
            "HP Support Assistant (Store)",
            "HP's diagnostics and update tool (Store version). Runs constantly in the background."),

        new(EntryKind.Uwp, ["AD2F1837.HPJumpStart", "AD2F1837.HPJumpStartLaunch"],
            "HP JumpStart",
            "HP's welcome/setup experience shown to new users. Safe to remove after initial setup."),

        new(EntryKind.Uwp, ["AD2F1837.myHP"],
            "myHP",
            "HP's hub app for tips and offers. Primarily promotional content."),

        new(EntryKind.Uwp, ["AD2F1837.HPWelcome"],
            "HP Welcome",
            "HP's first-run welcome experience. Safe to remove once set up."),

        new(EntryKind.Uwp, ["AD2F1837.HPEasyClean"],
            "HP Easy Clean",
            "HP's touchpad/screen cleaning mode app. Safe to remove if unused."),

        new(EntryKind.Uwp, ["AD2F1837.HPQuickDrop"],
            "HP Quick Drop",
            "HP's file transfer tool between phone and PC. Remove if unused."),

        new(EntryKind.Uwp, ["AD2F1837.HPPrivacySettings"],
            "HP Privacy Settings",
            "HP's privacy configuration app. Windows Settings covers everything it does."),

        new(EntryKind.Uwp, ["AD2F1837.HPAudioCenter"],
            "HP Audio Center",
            "HP's audio control panel. Your audio drivers still work without this app."),

        new(EntryKind.Uwp, ["AD2F1837.HPPCHardwareDiagnosticsWindows"],
            "HP PC Hardware Diagnostics",
            "HP's hardware testing tool. Only needed if troubleshooting hardware faults."),

        new(EntryKind.Uwp, ["AD2F1837.HPPowerManager"],
            "HP Power Manager",
            "HP's power plan app. Windows power settings cover everything this does."),

        new(EntryKind.Uwp, ["AD2F1837.HPPrinterControl"],
            "HP Smart (Store)",
            "HP's printer management app. Only needed if you actively use an HP printer."),

        new(EntryKind.Uwp, ["AD2F1837.HPBluetoothPairingAssistant"],
            "HP Bluetooth Pairing Assistant",
            "HP's Bluetooth setup wizard. Windows Bluetooth settings replace this."),

        new(EntryKind.Uwp, ["DellInc.DellUpdate", "DellInc.DellCommandUpdate"],
            "Dell Update (Store)",
            "Dell's driver update tool (Store version). Safe to remove."),

        new(EntryKind.Uwp, ["DellInc.DellOptimizer"],
            "Dell Optimizer (Store)",
            "Dell's AI-based optimisation app. Runs background services constantly."),

        new(EntryKind.Uwp, ["DellInc.PartnerPromo"],
            "Dell Partner Promo",
            "Dell's promotional/advertising app. Pure bloatware — safe to remove."),

        new(EntryKind.Uwp, ["DellInc.DellMobileConnect"],
            "Dell Mobile Connect (Store)",
            "Dell's phone-to-PC link app. Remove if you don't use this feature."),

        new(EntryKind.Uwp, ["DellInc.DellSupportAssist"],
            "Dell SupportAssist (Store)",
            "Dell's support and diagnostics app (Store version). Runs background scans constantly."),

        new(EntryKind.Uwp, ["DellInc.DellDigitalDelivery"],
            "Dell Digital Delivery (Store)",
            "Delivers pre-purchased Dell software. Safe to remove once set up."),

        new(EntryKind.Uwp, ["DellInc.DellCustomerConnect"],
            "Dell Customer Connect",
            "Dell's customer engagement app. Primarily promotional content."),

        new(EntryKind.Uwp, ["E046963F.LenovoCompanion"],
            "Lenovo Companion (Store)",
            "Older Lenovo hub app. Superseded by Lenovo Vantage on newer machines."),

        new(EntryKind.Uwp, ["E046963F.LenovoVantage", "LenovoGroup.LenovoSettings"],
            "Lenovo Vantage / Settings (Store)",
            "Lenovo's hardware control app. Remove with caution on Lenovo laptops."),

        new(EntryKind.Uwp, ["E046963F.LenovoID"],
            "Lenovo ID",
            "Lenovo's account login app. Not required for the PC to function."),

        new(EntryKind.Uwp, ["E046963F.LenovoSettingsforEnterprise"],
            "Lenovo Settings (Enterprise)",
            "Lenovo's settings app pre-loaded on enterprise machines. Safe to remove if unused."),

        new(EntryKind.Uwp, ["B9ECED6F.ASUSPCAssistant"],
            "ASUS PC Assistant (Store)",
            "Older ASUS support app. Superseded by MyASUS. Safe to remove."),

        new(EntryKind.Uwp, ["AsusTek.MyASUS", "AsusTek.MyASUS_lite"],
            "MyASUS",
            "ASUS's hub app for support, battery health, and updates."),

        new(EntryKind.Uwp, ["AsusTek.ASUSSystemDiagnosis"],
            "ASUS System Diagnosis",
            "ASUS hardware diagnostics tool. Safe to remove unless actively troubleshooting."),

        new(EntryKind.Uwp, ["AsusTek.ASUSBatteryHealthCharging"],
            "ASUS Battery Health Charging",
            "Controls ASUS battery charge limit (e.g. 80% cap). Remove only if unused."),

        new(EntryKind.Uwp, ["AsusTek.ASUSArmouryCreate"],
            "ASUS Armoury Crate (Store)",
            "ASUS's RGB and performance hub. Runs background services. Safe to remove if unused."),

        new(EntryKind.Uwp, ["AcerIncorporated.AcerCollection", "AcerIncorporated.AcerCollectionS"],
            "Acer Collection",
            "Acer's app launcher and promotional hub. Primarily bloatware. Safe to remove."),

        new(EntryKind.Uwp, ["AcerIncorporated.QuickAccess", "AcerIncorporated.AcerQuickAccess"],
            "Acer Quick Access",
            "Acer's shortcut manager. Safe to remove — Windows handles hotkeys fine."),

        new(EntryKind.Uwp, ["AcerIncorporated.AcerPortal"],
            "Acer Portal",
            "Acer's support and promotional portal. Safe to remove."),

        new(EntryKind.Uwp, ["AcerIncorporated.AcerUserExperienceImprovementProgram"],
            "Acer UEIP",
            "Acer's telemetry/data collection program. Safe to remove."),

        new(EntryKind.Uwp, ["Acer.AcerCarecenter", "AcerIncorporated.AcerCarecenter"],
            "Acer Care Center (Store)",
            "Acer's diagnostics and support app. Safe to remove if you manage drivers manually."),

        new(EntryKind.Uwp, ["SAMSUNGELECTRONICSCO.LTD.SamsungSettings"],
            "Samsung Settings",
            "Samsung's PC settings hub. Windows Settings covers its features."),

        new(EntryKind.Uwp, ["SAMSUNGELECTRONICSCO.LTD.SamsungUpdate"],
            "Samsung Update",
            "Samsung's driver update app. Drivers can be updated via Windows Update."),

        new(EntryKind.Uwp, ["SAMSUNGELECTRONICSCO.LTD.SamsungPC", "SAMSUNGELECTRONICSCO.LTD.SamsungPCPortal"],
            "Samsung PC Experience",
            "Samsung's welcome and promotional hub. Safe to remove."),

        new(EntryKind.Uwp, ["SAMSUNGELECTRONICSCO.LTD.SamsungFlow"],
            "Samsung Flow",
            "Samsung's phone-to-PC connection app. Remove if you don't use it."),

        new(EntryKind.Uwp, ["SAMSUNGELECTRONICSCO.LTD.SamsungQuickShare"],
            "Samsung Quick Share",
            "Samsung's file sharing app. Windows supports Nearby Sharing natively."),

        new(EntryKind.Uwp, ["SAMSUNGELECTRONICSCO.LTD.SamsungGalaxyStore"],
            "Samsung Galaxy Store",
            "Samsung's app store for PC. Only useful if you sync Samsung Android devices."),

        new(EntryKind.Uwp, ["MSIGroup.MSIAPP", "MSIGroup.MSICenter"],
            "MSI Center (Store)",
            "MSI's hardware monitoring and RGB hub. Safe to remove if unused."),

        new(EntryKind.Uwp, ["MSIGroup.MSIDragonCenter"],
            "MSI Dragon Center",
            "Older MSI performance hub (replaced by MSI Center). Safe to remove."),

        new(EntryKind.Uwp, ["CyberLinkCorp.ac.PowerDirectorforHP", "CyberLinkCorp.ac.PowerDirectorEssential"],
            "CyberLink PowerDirector",
            "A video editing trial pre-installed by some OEMs. Safe to remove."),

        new(EntryKind.Uwp, ["CyberLinkCorp.ac.PhotoDirectorEssential"],
            "CyberLink PhotoDirector",
            "A photo editing trial pre-installed by some OEMs. Safe to remove."),

        new(EntryKind.Uwp, ["CyberLinkCorp.ac.MediaSuiteEssentials"],
            "CyberLink Media Suite",
            "A media playback bundle pre-installed by some OEMs. VLC replaces it all."),

        new(EntryKind.Uwp, ["WildGames.WildTangentGamesApp-wildgames", "WildTangent.WildTangentGamesApp"],
            "WildTangent Games (Store)",
            "A game trial launcher pre-installed by many OEMs. Safe to remove."),

        new(EntryKind.Uwp, ["DolbyLaboratories.DolbyAccess"],
            "Dolby Access",
            "Dolby Atmos/audio enhancement app. Remove only if you don't use Dolby sound."),

        new(EntryKind.Uwp, ["McAfee.McAfeeSecurityApp", "mcafee.mtp", "TcT.McAfeePersonalSecurity"],
            "McAfee Security (Store trial)",
            "McAfee antivirus trial. Windows Defender provides full protection without it."),

        new(EntryKind.Uwp, ["NortonLifeLock.NortonSecurity", "Symantec.NortonSecurity360"],
            "Norton Security (Store trial)",
            "Norton antivirus trial. Windows Defender is a free and capable replacement."),

        new(EntryKind.Uwp, ["Tile.TileWindowsApplication"],
            "Tile",
            "Bluetooth tracker companion. Only needed if you own Tile tracking devices."),

        new(EntryKind.Uwp, ["SpeedTest.SpeedtestbyOokla"],
            "Speedtest by Ookla",
            "Pre-installed on some OEM machines. Speedtest works in any browser."),

        new(EntryKind.Uwp, ["Fitbit.FitbitCoach"],
            "Fitbit Coach",
            "Fitbit fitness app pre-installed on some OEM machines. Safe to remove."),

        // ═══════════════════════════════════════════════════════════════════
        // WIN32 APPS  (detected via registry, removed via winget / UninstallString)
        // ═══════════════════════════════════════════════════════════════════

        // ── Dell Win32 ────────────────────────────────────────────────────
        new(EntryKind.Win32,
            ["Dell SupportAssist", "SupportAssist for Home PCs", "SupportAssist for Business PCs"],
            "Dell SupportAssist",
            "Dell's support and diagnostics app. Runs background scans and network calls 24/7. Safe to remove — your hardware still works perfectly without it.",
            "Dell.SupportAssist"),

        new(EntryKind.Win32,
            ["Dell Command | Update", "Dell Command Update"],
            "Dell Command | Update",
            "Dell's driver and firmware update tool. Safe to remove — updates are on Dell's website or Windows Update.",
            "Dell.CommandUpdate"),

        new(EntryKind.Win32,
            ["Dell Display Manager"],
            "Dell Display Manager",
            "Dell's monitor control app. Remove if you don't use Dell monitors or don't need its snap/brightness features.",
            "Dell.DisplayManager"),

        new(EntryKind.Win32,
            ["Dell Peripheral Manager"],
            "Dell Peripheral Manager",
            "Dell's mouse/keyboard companion app. Remove if you manage peripherals through Windows directly.",
            "Dell.PeripheralManager"),

        new(EntryKind.Win32,
            ["Dell Power Manager"],
            "Dell Power Manager",
            "Dell's battery and power plan app. Windows power settings cover everything it does.",
            "Dell.PowerManager"),

        new(EntryKind.Win32,
            ["Dell Digital Delivery"],
            "Dell Digital Delivery",
            "Delivers software pre-purchased with your Dell. Safe to remove once initial setup is complete.",
            "Dell.DigitalDelivery"),

        new(EntryKind.Win32,
            ["Dell Optimizer"],
            "Dell Optimizer (Win32)",
            "Dell's AI-based performance optimizer. Runs many background processes constantly. Safe to remove.",
            "Dell.Optimizer"),

        new(EntryKind.Win32,
            ["Dell Mobile Connect"],
            "Dell Mobile Connect",
            "Dell's phone-to-PC mirroring app. Remove if you don't use this feature.",
            "Dell.MobileConnect"),

        new(EntryKind.Win32,
            ["Dell Trusted Device"],
            "Dell Trusted Device",
            "Dell's enterprise BIOS verification agent. Rarely needed on personal machines.",
            null),

        new(EntryKind.Win32,
            ["Dell IntelliSense"],
            "Dell IntelliSense",
            "Dell's hardware sensing service bundled with Dell Optimizer. Safe to remove.",
            null),

        // ── HP Win32 ──────────────────────────────────────────────────────
        new(EntryKind.Win32,
            ["HP Support Assistant", "HP Support Framework", "HPSA Service"],
            "HP Support Assistant",
            "HP's diagnostics and update app. Runs constantly in the background and makes frequent network calls. Updates are available on HP's website.",
            "HP.HPSupportAssistant"),

        new(EntryKind.Win32,
            ["HP Wolf Security", "HP Wolf Pro Security", "HP Sure Sense"],
            "HP Wolf Security / Sure Sense",
            "HP's bundled security suite. Windows Defender provides full protection without it.",
            "HP.WolfSecurity"),

        new(EntryKind.Win32,
            ["HP Connection Optimizer"],
            "HP Connection Optimizer",
            "HP's network switching app that runs in the background. Windows handles Wi-Fi/LAN switching natively.",
            null),

        new(EntryKind.Win32,
            ["HP Notifications"],
            "HP Notifications",
            "HP's notification service for support alerts and promotions. Safe to remove.",
            null),

        new(EntryKind.Win32,
            ["HP System Event Utility"],
            "HP System Event Utility",
            "Handles HP-specific hotkeys. Standard function keys still work without it.",
            null),

        new(EntryKind.Win32,
            ["HP Client Security Manager", "HP Security Manager"],
            "HP Client Security Manager",
            "HP's enterprise security management tool. Rarely needed on home PCs.",
            null),

        new(EntryKind.Win32,
            ["HP Documentation", "HP User Guides"],
            "HP Documentation",
            "HP's manual viewer. The same content is on HP's support website.",
            null),

        new(EntryKind.Win32,
            ["HP Audio Switch"],
            "HP Audio Switch",
            "HP's audio device switching utility. Windows audio settings handle this natively.",
            null),

        new(EntryKind.Win32,
            ["Poly Lens", "Plantronics Hub"],
            "Poly Lens / Plantronics Hub",
            "HP's headset companion app (Poly brand). Only needed if you use Poly/Plantronics headsets.",
            null),

        new(EntryKind.Win32,
            ["HP Recovery Manager"],
            "HP Recovery Manager",
            "HP's factory-reset tool. Safe to remove once the machine is set up to your liking.",
            null),

        // ── Lenovo Win32 ──────────────────────────────────────────────────
        new(EntryKind.Win32,
            ["Lenovo System Update", "Lenovo System Interface Foundation"],
            "Lenovo System Update",
            "Lenovo's driver update tool. Runs background services. Updates can be done manually on Lenovo's website.",
            "Lenovo.SystemUpdate"),

        new(EntryKind.Win32,
            ["Lenovo Vantage Service"],
            "Lenovo Vantage Service",
            "The background service for Lenovo Vantage. Safe to remove if you don't use Lenovo Vantage.",
            null),

        new(EntryKind.Win32,
            ["Lenovo Access Connections"],
            "Lenovo Access Connections",
            "Lenovo's Wi-Fi and network profile manager. Windows network settings replace it entirely.",
            null),

        new(EntryKind.Win32,
            ["Lenovo Utility"],
            "Lenovo Utility",
            "A collection of Lenovo-specific utilities. Safe to remove if you don't use Lenovo hotkeys.",
            null),

        new(EntryKind.Win32,
            ["Lenovo PM Service", "Lenovo Power Management Driver"],
            "Lenovo PM Service",
            "Lenovo's power management background service. Battery still works without it.",
            null),

        new(EntryKind.Win32,
            ["Lenovo Hotkeys"],
            "Lenovo Hotkeys",
            "Lenovo's hotkey manager. Standard keys still function without it.",
            null),

        new(EntryKind.Win32,
            ["ImController"],
            "Lenovo ImController",
            "Lenovo's infrastructure manager that keeps OEM software running. Safe to remove.",
            null),

        new(EntryKind.Win32,
            ["Lenovo Migration Assistant"],
            "Lenovo Migration Assistant",
            "Lenovo's PC-to-PC data transfer tool. Safe to remove once you've transferred your files.",
            null),

        // ── ASUS Win32 ────────────────────────────────────────────────────
        new(EntryKind.Win32,
            ["ASUS Live Update", "ASUS Update"],
            "ASUS Live Update",
            "ASUS's auto-updater for drivers and BIOS. Runs in the background. Updates are on ASUS's website.",
            null),

        new(EntryKind.Win32,
            ["ASUS System Control Interface"],
            "ASUS System Control Interface",
            "ASUS's background driver manager for OEM features. Safe to remove if you don't use MyASUS or Armoury Crate.",
            null),

        new(EntryKind.Win32,
            ["Armoury Crate", "ASUS Armoury Crate"],
            "ASUS Armoury Crate (Win32)",
            "ASUS's RGB lighting and performance hub. Installs many background services. Safe to remove if you don't use RGB/game profiles.",
            "Asus.ArmouryCrate"),

        new(EntryKind.Win32,
            ["ASUS GiftBox"],
            "ASUS GiftBox",
            "ASUS's promotional app launcher pre-installed on new machines. Pure bloatware — safe to remove.",
            null),

        new(EntryKind.Win32,
            ["ASUS Aura Sync"],
            "ASUS Aura Sync",
            "ASUS's RGB lighting control for peripherals and motherboards. Safe to remove if you don't use RGB.",
            null),

        new(EntryKind.Win32,
            ["ASUS GPU Tweak"],
            "ASUS GPU Tweak",
            "ASUS's GPU overclocking and monitoring app. Only needed if you actively tweak your ASUS GPU.",
            null),

        // ── Acer Win32 ────────────────────────────────────────────────────
        new(EntryKind.Win32,
            ["Acer Care Center"],
            "Acer Care Center (Win32)",
            "Acer's diagnostics and support app. Safe to remove if you manage drivers manually.",
            "Acer.CareCenter"),

        new(EntryKind.Win32,
            ["Acer Product Registration"],
            "Acer Product Registration",
            "Acer's product registration prompt. Registration is optional and can be done on Acer's website.",
            null),

        new(EntryKind.Win32,
            ["Acer Configuration Manager"],
            "Acer Configuration Manager",
            "Acer's enterprise configuration tool pre-installed on some consumer models. Safe to remove.",
            null),

        new(EntryKind.Win32,
            ["Acer Quick Access"],
            "Acer Quick Access (Win32)",
            "Acer's hotkey shortcut manager. Standard Windows hotkeys still work without it.",
            null),

        // ── Samsung Win32 ─────────────────────────────────────────────────
        new(EntryKind.Win32,
            ["Samsung Update", "SW Update"],
            "Samsung Update (Win32)",
            "Samsung's driver and software update tool. Updates are available on Samsung's support website.",
            null),

        new(EntryKind.Win32,
            ["Samsung DeX"],
            "Samsung DeX",
            "Samsung's desktop experience for connecting Galaxy phones. Remove if unused.",
            null),

        // ── MSI Win32 ─────────────────────────────────────────────────────
        new(EntryKind.Win32,
            ["MSI Center", "MSI Dragon Center", "Dragon Center"],
            "MSI Center / Dragon Center (Win32)",
            "MSI's performance and RGB hub. Runs background services. Safe to remove if you don't use MSI profiles.",
            "MSI.MSICenter"),

        new(EntryKind.Win32,
            ["Nahimic"],
            "Nahimic Audio",
            "A 3D audio enhancement app pre-installed by MSI, ASUS, and others. Remove if you don't use the effects.",
            null),

        // ── Security trials (Win32) ───────────────────────────────────────
        new(EntryKind.Win32,
            ["McAfee Security", "McAfee LiveSafe", "McAfee Total Protection",
             "McAfee Internet Security", "McAfee WebAdvisor"],
            "McAfee Security (Win32 trial)",
            "McAfee antivirus trial pre-installed by many OEMs. Windows Defender provides full protection for free.",
            "McAfee.McAfeeSecurity"),

        new(EntryKind.Win32,
            ["Norton 360", "Norton Security", "Norton AntiVirus",
             "NortonLifeLock", "Norton Internet Security"],
            "Norton Security (Win32 trial)",
            "Norton antivirus trial pre-installed by many OEMs. Windows Defender is a free and capable replacement.",
            "NortonLifeLock.Norton360"),

        new(EntryKind.Win32,
            ["Avast Free Antivirus", "Avast Premier", "Avast Security"],
            "Avast Antivirus",
            "Avast antivirus suite. Can run aggressively in the background. Windows Defender is sufficient for most users.",
            "Avast.Avast"),

        new(EntryKind.Win32,
            ["AVG AntiVirus", "AVG Internet Security", "AVG Ultimate"],
            "AVG AntiVirus",
            "AVG antivirus suite (same company as Avast). Windows Defender provides equivalent protection.",
            "AVG.AntiVirus"),

        // ── CyberLink Win32 ───────────────────────────────────────────────
        new(EntryKind.Win32,
            ["CyberLink PowerDVD", "CyberLink MediaShow",
             "CyberLink YouCam", "CyberLink Power2Go"],
            "CyberLink Suite (Win32)",
            "CyberLink media apps pre-installed by OEMs as trials with limited features. Safe to remove.",
            null),

        // ── WildTangent Win32 ─────────────────────────────────────────────
        new(EntryKind.Win32,
            ["WildTangent Games"],
            "WildTangent Games (Win32)",
            "A game trial launcher pre-installed by HP, Acer, Dell, and others. Safe to remove.",
            "WildTangent.WildTangentGamesApp"),

        // ── Misc OEM Win32 ────────────────────────────────────────────────
        new(EntryKind.Win32,
            ["ExpressVPN"],
            "ExpressVPN (bundled)",
            "VPN app bundled on some OEM systems. Remove if you didn't choose to install it.",
            null),

        new(EntryKind.Win32,
            ["Dropbox"],
            "Dropbox",
            "Cloud storage app pre-installed by some OEMs. Remove if you don't use Dropbox.",
            "Dropbox.Dropbox"),

        new(EntryKind.Win32,
            ["Evernote"],
            "Evernote",
            "Note-taking app pre-installed by some OEMs. Remove if you don't use it.",
            "Evernote.Evernote"),

        new(EntryKind.Win32,
            ["RealPlayer", "RealDownloader", "RealTimes"],
            "RealPlayer",
            "Legacy media player still appearing on some OEM installs. VLC is a better alternative.",
            null),

        new(EntryKind.Win32,
            ["Booking.com"],
            "Booking.com",
            "Travel booking app pre-installed on some machines. Accessible via browser without this app.",
            null),
    ];

    // ── Scan ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans for installed apps — both UWP (AppxPackage) and Win32 (registry).
    /// Only returns apps that are actually present on this machine.
    /// </summary>
    public async Task<List<BloatwareEntry>> ScanAsync()
    {
        _log.Info("BloatwareService", "Scanning for pre-installed and OEM apps...");

        // Run both scans in parallel
        var uwpTask   = GetInstalledUwpPackageNamesAsync();
        var win32Task = Task.Run(GetInstalledWin32Apps);
        await Task.WhenAll(uwpTask, win32Task);

        HashSet<string>              uwpPackages = uwpTask.Result;
        Dictionary<string, Win32App> win32Apps   = win32Task.Result;

        var found     = new List<BloatwareEntry>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _catalogue)
        {
            if (!seenNames.Add(entry.DisplayName)) continue; // deduplicate

            if (entry.Kind == EntryKind.Uwp)
            {
                string? matched = entry.Identifiers.FirstOrDefault(uwpPackages.Contains);
                if (matched == null) continue;

                found.Add(new BloatwareEntry
                {
                    DisplayName = entry.DisplayName,
                    Description = entry.Description,
                    PackageName = matched,
                    IsWin32     = false,
                    IsInstalled = true,
                });
            }
            else // Win32
            {
                Win32App? matched = null;
                foreach (var pattern in entry.Identifiers)
                {
                    matched = win32Apps.Values.FirstOrDefault(a =>
                        a.DisplayName.Contains(pattern, StringComparison.OrdinalIgnoreCase));
                    if (matched != null) break;
                }
                if (matched == null) continue;

                found.Add(new BloatwareEntry
                {
                    DisplayName     = entry.DisplayName,
                    Description     = entry.Description,
                    PackageName     = matched.DisplayName,   // registry name, used for logging
                    IsWin32         = true,
                    WingetId        = entry.WingetId,
                    UninstallString = matched.UninstallString,
                    IsInstalled     = true,
                });
            }
        }

        _log.Info("BloatwareService", $"Scan complete — {found.Count} pre-installed app(s) found.");
        return found;
    }

    // ── Remove ────────────────────────────────────────────────────────────────

    public async Task<TweakResult> RemoveAsync(BloatwareEntry entry)
    {
        if (entry.IsWin32)
            return await RemoveWin32Async(entry);
        return await RemoveUwpAsync(entry.PackageName);
    }

    // Overload kept for callers that pass just a package name (UWP path)
    public async Task<TweakResult> RemoveAsync(string packageName)
        => await RemoveUwpAsync(packageName);

    private async Task<TweakResult> RemoveUwpAsync(string packageName)
    {
        _log.Info("BloatwareService", $"Removing UWP: {packageName}");
        var safeName = packageName.Replace("'", "''");

        return await Task.Run(async () =>
        {
            try
            {
                using var ps = new Process();
                ps.StartInfo = new ProcessStartInfo
                {
                    FileName               = "powershell.exe",
                    Arguments              = $"-NonInteractive -NoProfile -Command " +
                                             $"\"Get-AppxPackage -Name '{safeName}' | Remove-AppxPackage\"",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                };
                ps.Start();
                var stdoutTask = ps.StandardOutput.ReadToEndAsync();
                var stderrTask = ps.StandardError.ReadToEndAsync();
                string out_ = await stdoutTask;
                string err  = await stderrTask;
                await ps.WaitForExitAsync();

                if (ps.ExitCode == 0)
                {
                    _log.Info("BloatwareService", $"UWP removed OK: {packageName}");
                    return TweakResult.Ok($"Removed: {packageName}");
                }
                var msg = (string.IsNullOrWhiteSpace(err) ? out_ : err).Trim();
                _log.Warn("BloatwareService", $"UWP remove failed ({packageName}): {msg}");
                return TweakResult.Fail(msg);
            }
            catch (Exception ex)
            {
                _log.Error("BloatwareService", $"UWP remove exception ({packageName})", ex);
                return TweakResult.FromException(ex);
            }
        });
    }

    private async Task<TweakResult> RemoveWin32Async(BloatwareEntry entry)
    {
        _log.Info("BloatwareService",
            $"Removing Win32: {entry.PackageName} (winget: {entry.WingetId ?? "none"})");

        return await Task.Run(async () =>
        {
            // ── Try winget first ──────────────────────────────────────────
            if (!string.IsNullOrEmpty(entry.WingetId))
            {
                var (ok, output) = await RunProcessAsync(
                    "winget",
                    $"uninstall --id \"{entry.WingetId}\" --silent --accept-source-agreements --force",
                    "BloatwareService");

                if (ok)
                {
                    _log.Info("BloatwareService", $"Win32 removed via winget: {entry.PackageName}");
                    return TweakResult.Ok($"Removed: {entry.PackageName}");
                }
                _log.Warn("BloatwareService", $"winget failed for {entry.WingetId}: {output}");
            }

            // ── Fallback: UninstallString from registry ───────────────────
            if (!string.IsNullOrEmpty(entry.UninstallString))
            {
                var (file, args) = ParseUninstallString(entry.UninstallString);

                // Append silent switches if not already present
                if (!args.Contains("/quiet",     StringComparison.OrdinalIgnoreCase) &&
                    !args.Contains("/S",         StringComparison.OrdinalIgnoreCase) &&
                    !args.Contains("/silent",    StringComparison.OrdinalIgnoreCase) &&
                    !args.Contains("/uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    args = (args + " /quiet /norestart").Trim();
                }

                var (ok, output) = await RunProcessAsync(file, args, "BloatwareService");
                if (ok)
                {
                    _log.Info("BloatwareService",
                        $"Win32 removed via UninstallString: {entry.PackageName}");
                    return TweakResult.Ok($"Removed: {entry.PackageName}");
                }
                _log.Warn("BloatwareService",
                    $"UninstallString failed for {entry.PackageName}: {output}");
                return TweakResult.Fail($"Uninstall failed: {output}");
            }

            return TweakResult.Fail(
                $"No removal method available for {entry.PackageName}. " +
                $"Please uninstall it manually via Settings > Apps.");
        });
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private static async Task<HashSet<string>> GetInstalledUwpPackageNamesAsync()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var ps = new Process();
            ps.StartInfo = new ProcessStartInfo
            {
                FileName               = "powershell.exe",
                Arguments              = "-NonInteractive -NoProfile -Command " +
                                         "\"Get-AppxPackage -User $env:USERNAME | " +
                                         "Select-Object -ExpandProperty Name\"",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            ps.Start();
            var stdoutTask = ps.StandardOutput.ReadToEndAsync();
            var stderrTask = ps.StandardError.ReadToEndAsync();
            ps.WaitForExit();
            string output = await stdoutTask;
            await stderrTask;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                names.Add(line.Trim());
        }
        catch { }
        return names;
    }

    private record Win32App(string DisplayName, string? UninstallString);

    private static Dictionary<string, Win32App> GetInstalledWin32Apps()
    {
        var apps = new Dictionary<string, Win32App>(StringComparer.OrdinalIgnoreCase);

        // Scan all four relevant registry locations
        TryReadUninstallHive(Registry.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", apps);
        TryReadUninstallHive(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", apps);
        TryReadUninstallHive(Registry.CurrentUser,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", apps);
        TryReadUninstallHive(Registry.CurrentUser,
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", apps);

        return apps;
    }

    private static void TryReadUninstallHive(
        RegistryKey hive, string path, Dictionary<string, Win32App> apps)
    {
        try
        {
            using var root = hive.OpenSubKey(path);
            if (root == null) return;

            foreach (var subName in root.GetSubKeyNames())
            {
                try
                {
                    using var sub = root.OpenSubKey(subName);
                    if (sub == null) continue;

                    var displayName = sub.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName)) continue;

                    // Skip invisible system components
                    if (sub.GetValue("SystemComponent") is int sc && sc == 1) continue;

                    // Skip Windows hotfix entries (KB + digits)
                    if (displayName.StartsWith("KB", StringComparison.Ordinal) &&
                        displayName.Length > 2 && char.IsDigit(displayName[2])) continue;

                    var uninstallString = sub.GetValue("UninstallString") as string;

                    // Use subName as key to avoid collisions on same DisplayName from
                    // both 32-bit and 64-bit hives
                    if (!apps.ContainsKey(subName))
                        apps[subName] = new Win32App(displayName, uninstallString);
                }
                catch { }
            }
        }
        catch { }
    }

    private static (string file, string args) ParseUninstallString(string s)
    {
        s = s.Trim();
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            if (end > 0)
                return (s[1..end], s[(end + 1)..].Trim());
        }
        int sp = s.IndexOf(' ');
        return sp > 0
            ? (s[..sp], s[(sp + 1)..].Trim())
            : (s, string.Empty);
    }

    private static async Task<(bool Success, string Output)> RunProcessAsync(
        string file, string args, string logTag)
    {
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName               = file,
                Arguments              = args,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            p.Start();
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            string stdout = await outTask;
            string stderr = await errTask;
            await p.WaitForExitAsync();
            string combined = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return (p.ExitCode == 0, combined.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
