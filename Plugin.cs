using System;
using Dalamud.Game.Command;
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

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IClientState clientState,
        IObjectTable objectTable,
        ITargetManager targetManager,
        INamePlateGui namePlateGui,
        ITextureProvider textureProvider,
        IFateTable fateTable,
        IPluginLog pluginLog)
    {
        PluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.pluginLog = pluginLog;

        Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        compassHud = new CompassHud(
            clientState, objectTable, targetManager, namePlateGui, textureProvider, fateTable, Config, pluginLog);
        configWindow = new ConfigWindow(this);

        windowSystem.AddWindow(configWindow);

        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the compass overlay. Use '/compass config' to open settings."
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
        compassHud.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("config", StringComparison.OrdinalIgnoreCase))
            configWindow.IsOpen = !configWindow.IsOpen;
        else
        {
            Config.Enabled = !Config.Enabled;
            Config.Save(PluginInterface);
        }
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
