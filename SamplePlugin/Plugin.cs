using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using System;
using System.IO;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Game.Gui.Dtr;
using SamplePlugin.Windows;
using SamplePlugin.Services;

namespace SamplePlugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    private const string CommandName = "/pmycommand";
    private IDtrBarEntry? dpsEntry;
    private ActService? actService;
    private bool wasInCombat = false;

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("SamplePlugin");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // You might normally want to embed resources and load them from the manifest stream
        var goatImagePath = Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName!, "goat.png");

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this, goatImagePath);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "A useful message to display in /xlhelp"
        });

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;

        // This adds a button to the plugin installer entry of this plugin which allows
        // toggling the display status of the configuration ui
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        // Adds another button doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        // Add a simple message to the log with level set to information
        // Use /xllog to open the log window in-game
        // Example Output: 00:57:54.959 | INF | [SamplePlugin] ===A cool log message from Sample Plugin===
        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");

        // Initialize DTR bar entry for DPS display
        InitializeDtrBar();

        // Initialize ACT service (but don't connect yet)
        actService = new ActService(Log);
        actService.OnDpsDataReceived += OnActDpsReceived;

        // Subscribe to Framework update to monitor combat state
        Framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        bool inCombat = Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];

        if (inCombat && !wasInCombat)
        {
            // Just entered combat - connect to OverlayPlugin
            Log.Information("[Plugin] Entered combat - connecting to OverlayPlugin");
            actService?.Connect();
            wasInCombat = true;
        }
        else if (!inCombat && wasInCombat)
        {
            // Just left combat - disconnect from OverlayPlugin
            Log.Information("[Plugin] Left combat - disconnecting from OverlayPlugin");
            actService?.Dispose();
            actService = new ActService(Log);
            actService.OnDpsDataReceived += OnActDpsReceived;
            wasInCombat = false;
            
            // Reset DPS display
            if (dpsEntry != null)
            {
                dpsEntry.Text = "- DPS";
            }
        }
    }

    private void OnActDpsReceived(object? sender, DpsDataEventArgs e)
    {
        if (dpsEntry != null)
        {
            var dpsValue = (int)Math.Round(e.PersonalDps);
            var jobName = e.JobId?.ToUpper() ?? "???";
            dpsEntry.Text = $"{jobName} {dpsValue} DPS";
        }
    }

    private void InitializeDtrBar()
    {
        try
        {
            dpsEntry = DtrBar.Get("DPS");
            dpsEntry.Text = "- DPS";
            dpsEntry.Shown = true;
            Log.Information("DTR bar entry initialized successfully");
        }
        catch (Exception ex)
        {
            dpsEntry = DtrBar.Get("DPS");
            dpsEntry.Text = "X DPS";
            dpsEntry.Shown = true;
            Log.Error(ex, "Failed to initialize DTR bar entry");
        }
    }

    public void Dispose()
    {
        // Unregister all actions to not leak anything during disposal of plugin
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        
        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);

        // Clean up DTR bar entry
        dpsEntry?.Remove();

        // Clean up ACT service
        actService?.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        // In response to the slash command, toggle the display status of our main ui
        MainWindow.Toggle();
    }
    
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
