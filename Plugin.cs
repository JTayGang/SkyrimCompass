using System;
using Dalamud.Game.Command;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace SkyrimCompass;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/compass";

    public IDalamudPluginInterface PluginInterface { get; }
    public Configuration Config { get; }

    private readonly ICommandManager commandManager;
    private readonly IPluginLog pluginLog;
    private readonly WindowSystem windowSystem = new("SkyrimCompass");
    private readonly CompassHud compassHud;
    private readonly ConfigWindow configWindow;
    private readonly IFontHandle jupiterFontHandle;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IClientState clientState,
        IObjectTable objectTable,
        ITargetManager targetManager,
        INamePlateGui namePlateGui,
        ITextureProvider textureProvider,
        IFateTable fateTable,
        ICondition condition,
        IDataManager dataManager,
        IPluginLog pluginLog)
    {
        PluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.pluginLog = pluginLog;

        Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // FFXIV's ornate serif font — loaded once, shared with CompassHud.
        jupiterFontHandle = pluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(
            new GameFontStyle(GameFontFamily.Jupiter, 18));

        compassHud = new CompassHud(
            clientState, objectTable, targetManager, namePlateGui, textureProvider, fateTable,
            condition, dataManager, Config, pluginLog, jupiterFontHandle);
        configWindow = new ConfigWindow(this);

        windowSystem.AddWindow(configWindow);

        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the compass. '/compass on' / '/compass off' to set it " +
                          "explicitly, '/compass config' for settings, " +
                          "'/compass debug' to log nearby objects (/xllog to view)."
        });

        pluginInterface.UiBuilder.Draw += OnDraw;
        pluginInterface.UiBuilder.OpenConfigUi += OnOpenConfig;
    }

    public void Dispose()
    {
        windowSystem.RemoveAllWindows();
        commandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfig;
        jupiterFontHandle.Dispose();
        compassHud.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (trimmed.Equals("config", StringComparison.OrdinalIgnoreCase))
            configWindow.IsOpen = !configWindow.IsOpen;
        else if (trimmed.Equals("debug", StringComparison.OrdinalIgnoreCase))
            compassHud.DumpNearbyObjects();
        else if (trimmed.Equals("on", StringComparison.OrdinalIgnoreCase))
            SetEnabled(true);
        else if (trimmed.Equals("off", StringComparison.OrdinalIgnoreCase))
            SetEnabled(false);
        else
            SetEnabled(!Config.Enabled);
    }

    // Idempotent: "on"/"on" twice in a row doesn't flip it back (unlike bare toggle).
    private void SetEnabled(bool enabled)
    {
        Config.Enabled = enabled;
        Config.Save(PluginInterface);
    }

    private void OnDraw()
    {
        try
        {
            windowSystem.Draw();
            compassHud.Draw();
        }
        catch (Exception ex)
        {
            pluginLog.Error(ex, "SkyrimCompass: unhandled exception in draw");
        }
    }

    private void OnOpenConfig() => configWindow.IsOpen = true;
}
