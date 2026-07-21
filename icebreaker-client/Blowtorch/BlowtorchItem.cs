using EFT.InventoryLogic;

namespace Manimal.Icebreaker.Blowtorch
{
    // the torch is a GENUINE PortableRangeFinderItemClass (parented straight under the
    // PortableRangeFinder node, no custom class): vanilla item creation keys visual
    // builders off exact types, so a subclass NREs in CreateItemAsync the moment
    // anything inspects or loot-spawns it (komradekid ships a whole CreateItemAsync
    // reimplementation to work around this; riding the vanilla class needs nothing).
    // our patches detect it by template id instead.
    internal static class BlowtorchIds
    {
        internal const string Tpl = "9a449693dff5334122ed7388";

        internal static bool IsTorch(Item item) => item != null && item.TemplateId == Tpl;
    }
}
