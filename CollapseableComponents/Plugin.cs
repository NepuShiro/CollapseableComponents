using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.NET.Common;
using BepInExResoniteShim;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;
using Renderite.Shared;

namespace CollapseableComponents;

[ResonitePlugin(PluginMetadata.GUID, PluginMetadata.NAME, PluginMetadata.VERSION, PluginMetadata.AUTHORS, PluginMetadata.REPOSITORY_URL)]
[BepInDependency(BepInExResoniteShim.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BasePlugin
{
    internal new static ManualLogSource Log = null!;

    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> DefaultExpanded;
    internal static ConfigEntry<bool> RunCleanUp;

    public override void Load()
    {
        Log = base.Log;

        Enabled = Config.Bind("General", "Enabled", true, "Enables/Disables the plugin.");
        DefaultExpanded = Config.Bind("General", "DefaultExpanded", true, "Whether the components are expanded by default, You can control this with a user variable 'User/Inspector_Collapse_Default' (bool) as well.");
        RunCleanUp = Config.Bind("General", "RunCleanUp", true, "Whether the plugin should cleanup escaped Expanders after 10 seconds.");

        HarmonyInstance.PatchAll();

        Log.LogInfo($"Plugin {PluginMetadata.GUID} is loaded!");
    }

    [HarmonyPatch(typeof(WorkerInspector), "BuildUIForComponent")]
    public class CollapseButtonPatch
    {
        public static void Postfix(Worker worker, WorkerInspector __instance, bool allowContainer)
        {
            if (!Enabled.Value) return;
            if (worker is Slot || allowContainer) return;

            Slot latest = __instance.Slot.Children.LastOrDefault();
            if (latest?.Children.FirstOrDefault() is not Slot headerSlot) return;

            Slot expander = latest.AddSlot("ExpanderFolder");
            expander.Tag = "CollapseableComponentsTag";

            UIBuilder ui = new UIBuilder(headerSlot);

            VerticalLayout layout = expander.AttachComponent<VerticalLayout>();
            layout.Spacing.Value = 4;

            List<Slot> children = new List<Slot>(latest.Children.Skip(1));
            foreach (Slot child in children)
            {
                child.Parent = expander;
            }

            RadiantUI_Constants.SetupEditorStyle(ui);
            ui.Style.MinWidth = 80f;

            Button button = ui.Button(new Uri("resdb:///dc53547406616593fd9601fae527aa450e82a1ff3606096161dd2038b7e219f3"), RadiantUI_Constants.Sub.PURPLE, RadiantUI_Constants.Hero.PURPLE);
            button.Slot.OrderOffset = -1;

            Expander exp = button.Slot.AttachComponent<Expander>();
            exp.SectionRoot.Target = expander;
            exp.IsExpanded = GetCollapseDefault(worker.LocalUser);

            if (button.Slot[0] is Slot image)
            {
                image.GetComponent<Image>()?.Destroy();
                image.GetComponent<SpriteProvider>()?.Destroy();

                RawImage rawImage = image.AttachComponent<RawImage>();
                rawImage.Texture.Target = image.GetComponent<StaticTexture2D>();
                rawImage.PreserveAspect.Value = true;

                BooleanValueDriver<RectOrientation> rectOri = image.AttachComponent<BooleanValueDriver<RectOrientation>>();
                rectOri.FalseValue.Value = RectOrientation.CounterClockwise90;
                rectOri.TrueValue.Value = RectOrientation.Default;
                rectOri.TargetField.Target = rawImage.Orientation;
                rectOri.State.DriveFrom(expander.ActiveSelf_Field);

                button.ColorDrivers[1].ColorDrive.Target = rawImage.Tint;

                if (image.GetComponent<RectTransform>() is RectTransform rect)
                {
                    rect.OffsetMin.Value = float2.Zero;
                    rect.OffsetMax.Value = float2.Zero;
                }
            }

            if (RunCleanUp.Value)
            {
                _ = RunOnce(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));
                    if (!Enabled.Value) return;

                    Log.LogDebug("Checking for escaped Expanders...");

                    World world = worker.World;
                    world.RunSynchronously(() =>
                    {
                        List<Slot> escapedChildren = new List<Slot>(world.RootSlot.GetChildrenWithTag("CollapseableComponentsTag").Where(x => x.Parent == world.RootSlot));
                        int count = 0;
                        foreach (Slot child in escapedChildren)
                        {
                            if (child != null)
                            {
                                child.Destroy();
                                count++;
                            }
                        }
                        Log.LogDebug($"Removed {count} escaped Expanders.");
                    });
                });
            }
        }
    }

    private static bool GetCollapseDefault(User localUser)
    {
        if (localUser?.Root?.Slot?.GetComponent<DynamicVariableSpace>(x => x.SpaceName.Value == "User")?.TryReadValue("User/Inspector_Collapse_Default", out bool collapseDefault) == true)
        {
            return !collapseDefault;
        }

        return DefaultExpanded.Value;
    }

    private static bool _isRunning;
    private static readonly Lock Sync = new Lock();

    private static async Task RunOnce(Func<Task> action)
    {
        lock (Sync)
        {
            if (_isRunning) return;
            _isRunning = true;
        }

        try
        {
            await action();
        }
        finally
        {
            lock (Sync)
            {
                _isRunning = false;
            }
        }
    }
}