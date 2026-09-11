using System;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;

namespace Manimal.Icebreaker
{
    // player-facing text. every string is a locale key the server writes into the
    // global locales (english everywhere, translated where a table exists — see
    // UiLocales / TranslatedLocales in IcebreakerMod). Localized() hands the key
    // back unchanged when it has no entry, so an older server without the keys
    // still gets the english copy passed in here. the usings mirror
    // IcebreakerChainDoor, the other caller of the Localized() extension.
    internal static class IcebreakerText
    {
        public static string Get(string key, string english)
        {
            try
            {
                var text = key.Localized();
                return string.IsNullOrEmpty(text) || text == key ? english : text;
            }
            catch
            {
                return english;
            }
        }
    }
}
