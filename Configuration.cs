using System;
using Dalamud.Configuration;
using GlamSource.Core;

namespace GlamSource;

[Serializable]
public class Configuration : Core.Configuration, IPluginConfiguration
{
    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
