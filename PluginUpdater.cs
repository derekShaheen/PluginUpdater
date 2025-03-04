using System.Threading.Tasks;
using ExileCore2;

namespace PluginUpdater;

public class PluginUpdater : BaseSettingsPlugin<PluginUpdaterSettings>
{
    public static PluginUpdater Instance;
    private Task _startupTask;

    public override bool Initialise()
    {
        Instance = this;
        Settings.GameController = GameController;
        _startupTask = Task.Run(Settings.PluginConfig.Startup);
        return true;
    }

    public override void Render()
    {
        if (_startupTask == null || !_startupTask.IsCompletedSuccessfully)
        {
            if (_startupTask?.IsFaulted == true)
            {
                DebugWindow.LogError(_startupTask.Exception.ToString());
            }

            return;
        }

        Settings.PluginConfig.Update();
    }
}