using Sandbox.Engine.Utils;
using System.Collections.Generic;
using System.Windows.Documents;
using TorchPlugin.Config;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace TorchPlugin
{
    public class Commands : CommandModule
    {
        private void Respond(string message)
        {
            Context?.Respond(message);
        }

        private void RespondWithHelp()
        {
            Respond($"{Plugin.PluginName} commands:");
            Respond("  !missilepatches help");
            Respond("  !missilepatches info");
            Respond("    Checks the current state of all the plugin's options");
            Respond("  !missilepatches phasing [Boolean]");
            Respond("    Checks or changes the state of the phasing patch");
            Respond("  !missilepatches damage [Boolean]");
            Respond("    Checks or changes the state of the damage patch");
            Respond("  !missilepatches backmove [Double]");
            Respond("    Checks or changes the collision point back-movement distance (avoids clipping)");
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

        [Command("missilepatches help", "Displays the commands available for SeMissilePatches")]
        [Permission(MyPromoteLevel.None)]
        public void Help()
        {
            RespondWithHelp();
        }
        [Command("missilepatches info", "Checks the current state of all the plugin's options")]
        [Permission(MyPromoteLevel.Admin)]
        public void Info()
        {
            Respond($"Patch for projectile phasing {(Plugin.Instance.Config.Phasing ? "ON" : "OFF")}");
            Respond($"Patch for projectile damage {(Plugin.Instance.Config.Damage ? "ON" : "OFF")}");
            Respond($"Collision point back-movement set to {Plugin.Instance.Config.BackMovement} meters");
        }

        [Command("missilepatches phasing", "Checks or changes the state of the phasing patch")]
        [Permission(MyPromoteLevel.Admin)]
        public void Phasing()
        {
            List<string> args = Context.Args;
            if (args.Count == 0)
            {
                Respond($"Patch for projectile phasing {(Plugin.Instance.Config.Phasing ? "ON" : "OFF")}");
            }
            else if (args.Count == 1)
            {
                bool newValue;
                if (TryParseBool(args[0], out newValue))
                {
                    Plugin.Instance.Config.Phasing = newValue;
                    Respond($"Patch for projectile phasing {(Plugin.Instance.Config.Phasing ? "ON" : "OFF")}");
                }
                else Respond($"ERROR: Could not parse '{args[0]}' as a Boolean.");
            }
            else Respond("ERROR: Invalid number of arguments.");
        }
        [Command("missilepatches damage", "Checks or changes the state of the damage patch")]
        [Permission(MyPromoteLevel.Admin)]
        public void Damage()
        {
            List<string> args = Context.Args;
            if (args.Count == 0)
            {
                Respond($"Patch for projectile damage {(Plugin.Instance.Config.Damage ? "ON" : "OFF")}");
            }
            else if (args.Count == 1)
            {
                bool newValue;
                if (TryParseBool(args[0], out newValue))
                {
                    Plugin.Instance.Config.Damage = newValue;
                    Respond($"Patch for projectile damage {(Plugin.Instance.Config.Damage ? "ON" : "OFF")}");
                }
                else Respond($"ERROR: Could not parse '{args[0]}' as a Boolean.");
            }
            else Respond("ERROR: Invalid number of arguments.");
        }
        [Command("missilepatches backmove", "Checks or changes the collision point back movement distance")]
        [Permission(MyPromoteLevel.Admin)]
        public void BackMove()
        {
            List<string> args = Context.Args;
            if (args.Count == 0)
            {
                Respond($"Collision point back-movement set to {Plugin.Instance.Config.BackMovement} meters");
            }
            else if (args.Count == 1)
            {
                double newValue;
                if (double.TryParse(args[0], out newValue))
                {
                    Plugin.Instance.Config.BackMovement = newValue;
                    Respond($"Collision point back-movement set to {Plugin.Instance.Config.BackMovement} meters");
                }
                else Respond($"ERROR: Could not parse '{args[0]}' as a Double.");
            }
            else Respond("ERROR: Invalid number of arguments.");
        }
    }
}