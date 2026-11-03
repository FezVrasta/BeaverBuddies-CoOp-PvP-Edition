using BeaverBuddies.MultiStart;
using Bindito.Core;
using System;
using System.Collections.Generic;
using System.Text;
using Timberborn.DiagnosticsUI;
using Timberborn.StartingLocationSystem;
using Timberborn.TemplateSystem;

namespace BeaverBuddies.Editor
{

    [Context("MapEditor")]
    public class EditorConfigurator : IConfigurator
    {
        public void Configure(IContainerDefinition containerDefinition)
        {
            SingletonManager.Reset();
            containerDefinition.Bind<StartingLocationNumberService>().AsSingleton();
            MultiStartConfigurator.Configure(containerDefinition);
            // The game only shows its FPS counter in games, though the counter behind it also runs here
            containerDefinition.Bind<FramesPerSecondPanel>().AsSingleton();
            if (DevTools.TestHarness.Active) containerDefinition.Bind<DevTools.TestHarness>().AsSingleton();
        }
    }
}
