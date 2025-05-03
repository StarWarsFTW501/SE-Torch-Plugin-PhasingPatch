using System.ComponentModel;

namespace TorchPlugin.Config
{
    public interface IPluginConfig : INotifyPropertyChanged
    {
        // Enables the phasing patch
        bool Phasing { get; set; }
        // Enables the damage patch
        bool Damage { get; set; }
        // Controls the back-movement of collision points
        double BackMovement { get; set; }
        // Enables checking for changes in patched game code (disable this on Proton/Linux)
        bool DetectCodeChanges { get; set; }


        // TODO: Add config properties here, then extend the implementing classes accordingly
    }
}