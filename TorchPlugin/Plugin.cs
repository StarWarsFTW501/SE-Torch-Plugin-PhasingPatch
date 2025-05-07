#define USE_HARMONY

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Controls;
using HarmonyLib;
using Sandbox.Game;
using TorchPlugin.Config;
using TorchPlugin.Logging;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.API.Session;
using Torch.Session;
using VRage.Utils;

namespace TorchPlugin
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public class Plugin : TorchPluginBase, IWpfPlugin
    {
        public const string PluginName = "SeMissilePatches";
        public static Plugin Instance { get; private set; }

        //public long Tick { get; private set; }

        public IPluginLogger Log => Logger;
        private static readonly IPluginLogger Logger = new PluginLogger(PluginName);

        public IPluginConfig Config => _config?.Data;
        private PersistentConfig<PluginConfig> _config;
        private static readonly string ConfigFileName = $"{PluginName}.cfg";

        // ReSharper disable once UnusedMember.Global
        public UserControl GetControl() => _control ?? (_control = new ConfigView());
        private ConfigView _control;

        private TorchSessionManager _sessionManager;


        private bool _initialized;

        // ReSharper disable once UnusedMember.Local
        // private readonly Commands commands = new Commands();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            Instance = this;

            Log.Info("Initializing plugin...");

            var configPath = Path.Combine(StoragePath, ConfigFileName);
            _config = PersistentConfig<PluginConfig>.Load(Log, configPath);

            // Force-initialize reflection
            RuntimeHelpers.RunClassConstructor(typeof(MyPatchUtilities).TypeHandle);

            if (!PatchHelpers.HarmonyPatchAll(Log, new Harmony(Name)))
            {
                Log.Error("Harmony patch failure - could not initialize plugin!");
                return;
            }

            _sessionManager = torch.Managers.GetManager<TorchSessionManager>();
            _sessionManager.SessionStateChanged += SessionStateChanged;

            Log.Info("Initialized.");
            _initialized = true;
        }

        private void SessionStateChanged(ITorchSession session, TorchSessionState newstate)
        {
            switch (newstate)
            {
                case TorchSessionState.Loading:
                    Log.Debug("Loading");
                    break;

                case TorchSessionState.Loaded:
                    Log.Debug("Loaded");
                    break;

                case TorchSessionState.Unloading:
                    Log.Debug("Unloading");
                    break;

                case TorchSessionState.Unloaded:
                    Log.Debug("Unloaded");
                    break;
            }
        }

        public override void Dispose()
        {
            if (_initialized)
            {
                Log.Debug("Disposing");

                _sessionManager.SessionStateChanged -= SessionStateChanged;
                _sessionManager = null;

                Log.Debug("Disposed");
            }

            Instance = null;

            base.Dispose();
        }
    }
}