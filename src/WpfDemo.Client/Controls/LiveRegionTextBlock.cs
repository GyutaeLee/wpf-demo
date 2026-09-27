using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace WpfDemo.Controls
{
    public sealed class LiveRegionTextBlock : TextBlock
    {
        public LiveRegionTextBlock()
        {
            AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if ((e.Property != TextProperty && e.Property != IsVisibleProperty) || !IsLoaded || !IsVisible) return;

            var peer = FrameworkElementAutomationPeer.FromElement(this) ?? FrameworkElementAutomationPeer.CreatePeerForElement(this);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
