using Shared.Config;
using Shared.Plugin;
using System.Collections.Generic;
using System.Windows.Documents;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace TorchPlugin
{
    public class Commands : CommandModule
    {
        private static IPluginConfig Config => Common.Config;

        private void Respond(string message)
        {
            Context?.Respond(message);
        }

        // TODO: Replace cmd with the name of your chat command
        // TODO: Implement subcommands as needed
        private void RespondWithHelp()
        {
            Respond("SePhasingPatch commands:");
            Respond("  !phasingpatch help");
            Respond("  !phasingpatch enabled [Boolean]");
            Respond("    Checks or changes the state of the plugin");
        }

        private void RespondWithInfo()
        {
            var config = Plugin.Instance.Config;
            Respond($"Patch for railgun phasing {(config.Enabled ? "ON" : "OFF")}");
            // TODO: Respond with your plugin settings
            // For example:
            //Respond($"custom_setting: {Format(config.CustomSetting)}");
        }


        private static bool TryParseBool(string text, out bool result)
        {
            switch (text.ToLower())
            {
                case "1":
                case "on":
                case "yes":
                case "y":
                case "true":
                case "t":
                    result = true;
                    return true;

                case "0":
                case "off":
                case "no":
                case "n":
                case "false":
                case "f":
                    result = false;
                    return true;
            }

            result = false;
            return false;
        }

        // ReSharper disable once UnusedMember.Global

        [Command("phasingpatch help", "Displays the commands available for SePhasingPatch")]
        [Permission(MyPromoteLevel.None)]
        public void Help()
        {
            RespondWithHelp();
        }
        
        // TODO: Subcommand
        // ReSharper disable once UnusedMember.Global
        [Command("phasingpatch enabled", "Checks or sets the enabled state of the plugin's patch for phasing.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Enabled()
        {
            List<string> args = Context.Args;
            if (args.Count == 0)
            {
                RespondWithInfo();
            }
            else if (args.Count == 1)
            {
                bool newValue;
                if (TryParseBool(args[0], out newValue))
                {
                    Config.Enabled = newValue;
                    RespondWithInfo();
                }
                else Respond($"ERROR: Could not parse '{args[0]}' as a Boolean.");
            }
            else Respond("ERROR: Invalid number of arguments.");
        }
    }
}