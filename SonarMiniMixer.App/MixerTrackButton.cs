using System.Windows.Automation.Peers;
using System.Windows.Controls.Primitives;

namespace SonarMiniMixer.App;

/// <summary>
/// Internal slider-track button that remains mouse-interactive without appearing
/// as a separate unnamed control in the accessibility tree.
/// </summary>
public sealed class MixerTrackButton : RepeatButton
{
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}
