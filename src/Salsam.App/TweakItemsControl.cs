using System.Collections.Specialized;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Salsam.App;

/// <summary>Expose the actual controls in each setting template to assistive technology.</summary>
public sealed class TweakItemsControl : ItemsControl
{
    public TweakItemsControl()
    {
        ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
                UIElementAutomationPeer.FromElement(this)?.InvalidatePeer();
        };
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CatalogPeer(this);

    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        UIElementAutomationPeer.FromElement(this)?.InvalidatePeer();
    }

    private sealed class CatalogPeer(TweakItemsControl owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(TweakItemsControl);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override List<AutomationPeer>? GetChildrenCore()
        {
            // Traverse realized templates directly instead of routing their
            // descendants through data-item/container peer wrappers. Stop at each
            // control's peer to retain its patterns and accessible descendants.
            var peers = new List<AutomationPeer>();
            for (var index = 0; index < owner.Items.Count; index++)
                if (owner.ItemContainerGenerator.ContainerFromIndex(index) is DependencyObject container)
                    for (var child = 0; child < VisualTreeHelper.GetChildrenCount(container); child++)
                        Collect(VisualTreeHelper.GetChild(container, child), peers);
            return peers.Count == 0 ? null : peers;
        }

        private static void Collect(DependencyObject visual, List<AutomationPeer> peers)
        {
            if (visual is UIElement element && UIElementAutomationPeer.CreatePeerForElement(element) is { } peer)
            {
                peers.Add(peer);
                return;
            }
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(visual); index++)
                Collect(VisualTreeHelper.GetChild(visual, index), peers);
        }
    }
}
