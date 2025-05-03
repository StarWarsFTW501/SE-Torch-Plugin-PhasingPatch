using System;
using Torch;
using Torch.Views;

namespace TorchPlugin.Config
{
    [Serializable]
    public class PluginConfig : ViewModel, IPluginConfig
    {
        private bool _phasing = true;
        private bool _damage = false;
        private bool _detectCodeChanges = true;
        private double _backMovement = 0.02;
        // TODO: Implement your config fields and add the default values for Torch here.
        //       Be more conservative with changes and introduce new features as disabled
        //       at first, so admins can enable them first on their test deployments.
        //       Once the feature is stable set the default here to true to enable for
        //       newly created Torch deployments.

        [Display(Order = 1, GroupName = "General", Name = "Enable phasing patch", Description = "Enable/disable the patch for missile projectile phasing")]
        public bool Phasing
        {
            get => _phasing;
            set
            {
                if (!value)
                {
                    MyPatchUtilities.ClearAllPhasingFixes();
                }
                SetValue(ref _phasing, value);
            }
        }

        [Display(Order = 2, GroupName = "General", Name = "Enable damage patch", Description = "Enable/disable the patch for missile projectile damage application")]
        public bool Damage
        {
            get => _damage;
            set => SetValue(ref _damage, value);
        }

        [Display(Order = 3, GroupName = "General", Name = "Collision back-move", Description = "How far back (meters) a collision point is moved to avoid clipping")]
        public double BackMovement
        {
            get => _backMovement;
            set => SetValue(ref _backMovement, value);
        }

        [Display(Order = 4, GroupName = "General", Name = "Detect code changes", Description = "Disable the plugin if any changes to the game code are detected before patching")]
        public bool DetectCodeChanges
        {
            get => _detectCodeChanges;
            set => SetValue(ref _detectCodeChanges, value);
        }


        // TODO: Encapsulate them as properties and define their Display properties
    }
}