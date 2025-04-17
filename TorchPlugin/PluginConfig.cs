using System;
using Shared.Config;
using Torch;
using Torch.Views;

namespace TorchPlugin
{
    [Serializable]
    public class PluginConfig : ViewModel, IPluginConfig
    {
        private bool phasing = true;
        private bool damage = true;
        private bool detectCodeChanges = true;
        private double backMovement = 0.02;
        // TODO: Implement your config fields and add the default values for Torch here.
        //       Be more conservative with changes and introduce new features as disabled
        //       at first, so admins can enable them first on their test deployments.
        //       Once the feature is stable set the default here to true to enable for
        //       newly created Torch deployments.

        [Display(Order = 1, GroupName = "General", Name = "Enable phasing patch", Description = "Enable/disable the patch for missile projectile phasing")]
        public bool Phasing
        {
            get => phasing;
            set => SetValue(ref phasing, value);
        }

        [Display(Order = 2, GroupName = "General", Name = "Enable damage patch", Description = "Enable/disable the patch for missile projectile damage application")]
        public bool Damage
        {
            get => damage;
            set => SetValue(ref damage, value);
        }

        [Display(Order = 3, GroupName = "General", Name = "Collision back-move", Description = "How far back (meters) a collision point is moved to avoid clipping")]
        public double BackMovement
        {
            get => backMovement;
            set => SetValue(ref backMovement, value);
        }

        [Display(Order = 4, GroupName = "General", Name = "Detect code changes", Description = "Disable the plugin if any changes to the game code are detected before patching")]
        public bool DetectCodeChanges
        {
            get => detectCodeChanges;
            set => SetValue(ref detectCodeChanges, value);
        }


        // TODO: Encapsulate them as properties and define their Display properties
    }
}