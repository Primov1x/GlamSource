using System;
using System.IO;
using System.Reflection;
using DalaMock.Core.Configuration;
using DalaMock.Core.Plugin;
using GlamSource;

var config = new MockDalamudConfiguration
{
    GamePathString = @"D:\FF\game\sqpack",
    PluginSavePathString = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher"), "DalaMock")
};

var mockContainer = new MockContainer(dalamudConfiguration: config, askPath: false);

var mockDalamudUi = mockContainer.GetMockUi();
var pluginLoader = mockContainer.GetPluginLoader();
var mockPlugin = pluginLoader.AddPlugin(typeof(Plugin));
await pluginLoader.StartPlugin(mockPlugin);
mockDalamudUi.Run();
