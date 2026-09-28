// <copyright file="PortalAccessSettings.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>
namespace PortalAccess
{
    using GameHelper.Plugin;

    public sealed class PortalAccessSettings : IPSettings
    {
        // New installs observe first; actual client changes require this explicit switch.
        public bool RestoreInteraction;
        public int RefreshIntervalMs = 1000;
        public int AreaDelayMs = 1500;
    }
}
