using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace WpfDemo.Controls
{
    public sealed class LiveRegionTextBlock : TextBlock
    {
        static LiveRegionTextBlock()
        {
            TextProperty.OverrideMetadata(typeof(LiveRegionTextBlock), new FrameworkPropertyMetadata(OnTextChanged));
        }

        public LiveRegionTextBlock()
        {
            AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
            IsVisibleChanged += (sender, args) => RaiseLiveRegionChanged();
        }

        private static void OnTextChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
        {
            ((LiveRegionTextBlock)element).RaiseLiveRegionChanged();
        }

        private void RaiseLiveRegionChanged()
        {
            if (!IsLoaded || !IsVisible) return;

            var peer = FrameworkElementAutomationPeer.FromElement(this) ?? FrameworkElementAutomationPeer.CreatePeerForElement(this);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
